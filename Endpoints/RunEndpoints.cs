using System.Security.Cryptography;
using System.Text.Json.Serialization;
using TowerOfArgentum.Api.Config;
using TowerOfArgentum.Api.Data;
using TowerOfArgentum.Api.Security;

namespace TowerOfArgentum.Api.Endpoints;

/// <summary>
/// Lo que manda el cliente al abrir una corrida.
/// </summary>
/// <remarks>
/// ⚠ <b>Sin mapa.</b> El área no se elige: con corrida roguelike continua, se
/// llega o no se llega. El parámetro <c>?mapa=</c> del contrato viejo ya no
/// existe en ningún endpoint.
/// </remarks>
public sealed record StartRequest(
    [property: JsonPropertyName("hashDatos")] string DataHash,
    [property: JsonPropertyName("plataforma")] string? Platform = null);

// ⚠ LOS [JsonPropertyName] SON EL CONTRATO, no decoración. Las propiedades van
//   en inglés como todo el código; los nombres que viajan por el cable siguen en
//   español porque el cliente ya está escrito contra ellos y los tres repos se
//   despliegan por separado. Sacar un atributo renombra un campo de la API sin
//   que nada falle al compilar: el cliente recibiría el campo en cero.
public sealed record StartResponse(
    long RunId,
    ulong Seed,
    [property: JsonPropertyName("datosVersion")] int DataVersion,
    [property: JsonPropertyName("abandonada")] long? Abandoned);

/// <summary>
/// Lo que reclama el cliente al terminar.
/// </summary>
/// <remarks>
/// ⚠ <b>No lleva oro ni precios.</b> El servidor deriva
/// <c>oroGanado = f(gano, oleada)</c>. Si el cliente mandara el oro, oro falso
/// compraría todos los planos y se volvería poder real — y la columna
/// <c>validado</c> no serviría de freno, porque el oro se gasta al instante.
/// </remarks>
// ⚠ ESTE RECORD NO TENÍA LOS [JsonPropertyName] Y ERA UN BUG MUDO.
//
// El cliente manda `gano` y `oleada` (ApiClient.FinishRunAsync); sin los
// atributos, ASP.NET liga por camelCase del nombre de la propiedad —`won` y
// `wave`—, así que los dos campos llegaban en su default: **toda corrida se
// cerraba como perdida en la oleada 0**. `area`, `ticks` y `log` se salvaban de
// casualidad, porque se escriben igual en los dos idiomas.
//
// Es exactamente el modo de falla que el comentario de arriba anticipa: no rompe
// la compilación, no tira error en runtime, y el campo llega en cero.
public sealed record FinishRequest(
    long RunId,
    [property: JsonPropertyName("gano")] bool Won,
    [property: JsonPropertyName("oleada")] int Wave,
    [property: JsonPropertyName("area")] int Area,
    [property: JsonPropertyName("ticks")] int Ticks,
    [property: JsonPropertyName("log")] string? Log);

public sealed record FinishResponse(
    [property: JsonPropertyName("oroGanado")] long GoldEarned,
    [property: JsonPropertyName("oroTotal")] long GoldTotal,
    [property: JsonPropertyName("oleada")] int Wave,
    [property: JsonPropertyName("gano")] bool Won);

/// <summary>
/// Un checkpoint de la corrida en curso.
/// </summary>
/// <remarks>
/// <para>Es el mismo trío que <c>SaveFile</c> escribe en disco —log, tick y hasta
/// dónde llegó—, pero contra el servidor. Con eso la corrida sobrevive a que se
/// cierre el juego y, sobre todo, <b>se puede seguir en otro dispositivo</b>.</para>
///
/// <para>⚠ El log va en base64, igual que en <see cref="FinishRequest"/>. Es un
/// blob binario y JSON no tiene forma de llevarlo de otra manera.</para>
/// </remarks>
public sealed record ProgressRequest(
    long RunId,
    [property: JsonPropertyName("oleada")] int Wave,
    [property: JsonPropertyName("area")] int Area,
    [property: JsonPropertyName("ticks")] int Ticks,
    [property: JsonPropertyName("tick")] int Tick,
    [property: JsonPropertyName("log")] string? Log);

/// <summary>La corrida abierta que devuelve <c>/run/actual</c>, lista para retomar.</summary>
public sealed record CurrentRunResponse(
    long RunId,
    ulong Seed,
    [property: JsonPropertyName("datosVersion")] int DataVersion,
    [property: JsonPropertyName("oleada")] int Wave,
    [property: JsonPropertyName("area")] int Area,
    [property: JsonPropertyName("ticks")] int Ticks,
    [property: JsonPropertyName("tick")] int SavedTick,
    [property: JsonPropertyName("log")] string? Log,
    [property: JsonPropertyName("logTruncado")] bool LogTruncated,
    [property: JsonPropertyName("plataforma")] string Platform,
    [property: JsonPropertyName("actualizada")] System.DateTime? UpdatedUtc);

public static class RunEndpoints
{
    public static void MapRuns(this WebApplication app)
    {
        app.MapPost("/run/start", StartAsync);
        app.MapPost("/run/finish", FinishAsync);

        // El checkpoint y la reanudación. Van después porque dependen de que la
        // corrida ya esté abierta.
        app.MapPost("/run/progress", ProgressAsync);
        app.MapGet("/run/actual", CurrentAsync);
    }

    private static async Task<IResult> StartAsync(
        StartRequest pedido,
        HttpContext http,
        TokenRepository tokens,
        RunRepository runs,
        SimulationDataCatalog simulation,
        CancellationToken ct)
    {
        long? idCuenta = await Authentication.AccountOfAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.Unauthorized, statusCode: StatusCodes.Status401Unauthorized);

        // ⚠ El hash de los datos de balance se verifica ACÁ, al abrir. Si el
        // cliente juega con otros números que el servidor, la corrida no es
        // reproducible y el ranking deja de comparar lo mismo. Rechazarla al
        // empezar es infinitamente mejor que rechazarla después de tres horas.
        if (!string.Equals(pedido.DataHash, simulation.Current.Hash, StringComparison.Ordinal))
        {
            return Results.Json(ErrorResponse.StaleData, statusCode: StatusCodes.Status409Conflict);
        }

        // ⚠ La seed la genera el SERVIDOR, con CSPRNG. Si la eligiera el cliente,
        // podría probar semillas hasta encontrar una fácil y reclamar la corrida
        // como buena.
        ulong seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

        // La plataforma la declara el cliente y no hay forma de comprobarla. Se
        // normaliza a los dos valores del ENUM en vez de confiar: cualquier otra
        // cosa entra como "pc", que es el default de la columna.
        string plataforma = pedido.Platform == "celular" ? "celular" : "pc";

        var (idNueva, abandonada) = await runs.AbrirAsync(
            idCuenta.Value, seed, simulation.Current.Version, simulation.Current.Hash, plataforma, ct);

        return Results.Ok(new StartResponse(idNueva, seed, simulation.Current.Version, abandonada));
    }

    private static async Task<IResult> FinishAsync(
        FinishRequest pedido,
        HttpContext http,
        TokenRepository tokens,
        RunRepository runs,
        AccountRepository cuentas,
        ServerData datos,
        CancellationToken ct)
    {
        long? idCuenta = await Authentication.AccountOfAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.Unauthorized, statusCode: StatusCodes.Status401Unauthorized);

        var run = await runs.FindOpenAsync(pedido.RunId, idCuenta.Value, ct);

        // Cubre las tres cosas de una: no existe, no es tuya, o ya está cerrada.
        // No se distinguen a propósito: decir "existe pero no es tuya" confirma
        // que el id acertó.
        if (run is null)
            return Results.NotFound(ErrorResponse.RunNotFound);

        var limites = datos.Limits;

        int oleada = Math.Clamp(pedido.Wave, 0, limites.MaxWave);
        int area = Math.Clamp(pedido.Area, limites.MinArea, limites.MaxArea);
        int ticks = Math.Max(0, pedido.Ticks);

        // ⚠ Solo se puede ganar en la ÚLTIMA oleada, matando al jefe final. Un
        // reclamo de "gané en la 40" es imposible por reglas del juego, así que se
        // rechaza entero en vez de corregirse en silencio: si llega uno, o hay un
        // bug del cliente o alguien está probando, y las dos cosas conviene verlas.
        if (pedido.Won && oleada != limites.MaxWave)
            return Results.BadRequest(ErrorResponse.InconsistentClaim);

        // El mismo decodificado que usa el checkpoint. Va en un método y no
        // repetido en los dos: dos copias de "qué hacer con un log ilegible" se
        // separan en la primera corrección.
        var (log, truncado) = DecodificarLog(pedido.Log, limites.MaxLogBytes);

        await runs.CloseAsync(pedido.RunId, pedido.Won, oleada, area, ticks, log, truncado, ct);

        long oroGanado = datos.RunGold(pedido.Won, oleada);
        await cuentas.AddGoldAsync(idCuenta.Value, oroGanado, ct);

        var cuenta = await cuentas.FindByIdAsync(idCuenta.Value, ct);

        return Results.Ok(new FinishResponse(
            oroGanado, cuenta?.Gold ?? 0, oleada, pedido.Won));
    }

    /// <summary>
    /// Guarda dónde va la corrida, sin cerrarla.
    /// </summary>
    /// <remarks>
    /// <para>Existe porque entre <c>/run/start</c> y <c>/run/finish</c> hay cien
    /// oleadas, que son horas. Sin esto, cerrar el juego en el medio pierde todo:
    /// no hay con qué retomar, no se sabe hasta dónde llegó el jugador, y no se
    /// puede probar el circuito sin jugarlo entero.</para>
    ///
    /// <para>⚠ <b>Sube el LOG, no un contador.</b> Es la diferencia entre un
    /// checkpoint verificable y uno que no lo es: "voy por la oleada 60" es un
    /// reclamo que el servidor no puede comprobar jamás; el log, con la seed que
    /// ya está en la fila, se re-simula. Guardar progreso mejora el anticheat en
    /// vez de abrirle un agujero.</para>
    ///
    /// <para>El progreso nunca retrocede: la guarda está en el <c>WHERE</c> del
    /// <c>UPDATE</c>. Ver <see cref="RunRepository.SaveProgressAsync"/>.</para>
    /// </remarks>
    private static async Task<IResult> ProgressAsync(
        ProgressRequest pedido,
        HttpContext http,
        TokenRepository tokens,
        RunRepository runs,
        ServerData datos,
        CancellationToken ct)
    {
        long? idCuenta = await Authentication.AccountOfAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.Unauthorized, statusCode: StatusCodes.Status401Unauthorized);

        var limites = datos.Limits;

        int oleada = Math.Clamp(pedido.Wave, 0, limites.MaxWave);
        int area = Math.Clamp(pedido.Area, limites.MinArea, limites.MaxArea);
        int ticks = Math.Max(0, pedido.Ticks);
        int tick = Math.Max(0, pedido.Tick);

        var (log, truncado) = DecodificarLog(pedido.Log, limites.MaxLogBytes);

        bool guardado = await runs.SaveProgressAsync(
            pedido.RunId, idCuenta.Value, oleada, area, ticks, tick, log, truncado, ct);

        // Un false acá son dos casos que no conviene distinguirle al cliente: la
        // corrida no existe o no es suya, y el checkpoint llegó atrasado —otro
        // dispositivo ya subió uno más avanzado—. En los dos el cliente tiene que
        // hacer lo mismo: dejar de mandar y consultar /run/actual.
        return guardado
            ? Results.Ok(new { ok = true })
            : Results.NotFound(ErrorResponse.RunNotFound);
    }

    /// <summary>
    /// La corrida abierta de la cuenta, con lo necesario para retomarla.
    /// </summary>
    /// <remarks>
    /// <para>Es lo que permite empezar en la PC y seguir en el celular. Devuelve
    /// seed + log + tick y el cliente <b>reproduce</b>: no hay estado
    /// serializado, cargar es re-simular desde el tick 0 a máxima velocidad.</para>
    ///
    /// <para>⚠ <b>Devuelve 404 si no hay ninguna abierta</b>, que no es un error:
    /// es la respuesta normal para un jugador que no dejó nada a medias. El
    /// cliente la usa para decidir entre "retomar" y "empezar".</para>
    ///
    /// <para>Va <c>datosVersion</c> adentro para que el cliente pueda negarse a
    /// reproducir contra otros números: un guardado es un log que se re-simula, y
    /// solo vale contra los datos con los que se jugó.</para>
    /// </remarks>
    private static async Task<IResult> CurrentAsync(
        HttpContext http,
        TokenRepository tokens,
        RunRepository runs,
        CancellationToken ct)
    {
        long? idCuenta = await Authentication.AccountOfAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.Unauthorized, statusCode: StatusCodes.Status401Unauthorized);

        var run = await runs.FindCurrentAsync(idCuenta.Value, ct);

        if (run is null)
            return Results.NotFound(ErrorResponse.RunNotFound);

        return Results.Ok(new CurrentRunResponse(
            run.Id,
            run.Seed,
            run.DataVersion,
            run.Wave,
            run.Area,
            run.Ticks,
            run.SavedTick,
            run.Log is null ? null : Convert.ToBase64String(run.Log),
            run.LogTruncated,
            run.Platform,
            run.UpdatedUtc));
    }

    /// <summary>
    /// Pasa el log de base64 a bytes, recortado al tope.
    /// </summary>
    /// <remarks>
    /// ⚠ Un base64 ilegible <b>no invalida nada</b>: se sigue sin log. Rechazar el
    /// pedido castigaría al jugador por un bug de serialización, y la corrida
    /// quedaría sin poder retomarse por un motivo que no es suyo. Lo que sí pasa
    /// es que sin log no se puede revalidar, y eso ya lo dice <c>validado = 0</c>.
    /// </remarks>
    private static (byte[]? Log, bool Truncado) DecodificarLog(string? base64, int maxBytes)
    {
        if (string.IsNullOrEmpty(base64))
            return (null, false);

        byte[] log;

        try
        {
            log = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return (null, false);
        }

        return log.Length > maxBytes
            ? (log[..maxBytes], true)
            : (log, false);
    }

}
