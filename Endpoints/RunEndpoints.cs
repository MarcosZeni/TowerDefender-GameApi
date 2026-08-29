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
    [property: JsonPropertyName("hashDatos")] string DataHash);

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
public sealed record FinishRequest(
    long RunId, bool Won, int Wave, int Area, int Ticks, string? Log);

public sealed record FinishResponse(
    [property: JsonPropertyName("oroGanado")] long GoldEarned,
    [property: JsonPropertyName("oroTotal")] long GoldTotal,
    [property: JsonPropertyName("oleada")] int Wave,
    [property: JsonPropertyName("gano")] bool Won);

public static class RunEndpoints
{
    public static void MapRuns(this WebApplication app)
    {
        app.MapPost("/run/start", StartAsync);
        app.MapPost("/run/finish", FinishAsync);
    }

    private static async Task<IResult> StartAsync(
        StartRequest pedido,
        HttpContext http,
        TokenRepository tokens,
        RunRepository runs,
        ApiConfig config,
        CancellationToken ct)
    {
        long? idCuenta = await Authentication.AccountOfAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.Unauthorized, statusCode: StatusCodes.Status401Unauthorized);

        // ⚠ El hash de los datos de balance se verifica ACÁ, al abrir. Si el
        // cliente juega con otros números que el servidor, la corrida no es
        // reproducible y el ranking deja de comparar lo mismo. Rechazarla al
        // empezar es infinitamente mejor que rechazarla después de tres horas.
        if (!string.IsNullOrEmpty(config.DataHash) &&
            !string.Equals(pedido.DataHash, config.DataHash, StringComparison.Ordinal))
        {
            return Results.Json(ErrorResponse.StaleData, statusCode: StatusCodes.Status409Conflict);
        }

        // ⚠ La seed la genera el SERVIDOR, con CSPRNG. Si la eligiera el cliente,
        // podría probar semillas hasta encontrar una fácil y reclamar la corrida
        // como buena.
        ulong seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

        var (idNueva, abandonada) = await runs.AbrirAsync(idCuenta.Value, seed, config.DataVersion, ct);

        return Results.Ok(new StartResponse(idNueva, seed, config.DataVersion, abandonada));
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

        byte[]? log = null;
        bool truncado = false;

        if (!string.IsNullOrEmpty(pedido.Log))
        {
            try
            {
                log = Convert.FromBase64String(pedido.Log);
            }
            catch (FormatException)
            {
                // Un log ilegible no invalida la corrida: se guarda sin él y
                // queda con validado = 0 para siempre, que es exactamente lo que
                // corresponde. Rechazar el finish castigaría al jugador por un
                // bug de serialización.
                log = null;
            }

            if (log is not null && log.Length > limites.MaxLogBytes)
            {
                log = log[..limites.MaxLogBytes];
                truncado = true;
            }
        }

        await runs.CloseAsync(pedido.RunId, pedido.Won, oleada, area, ticks, log, truncado, ct);

        long oroGanado = datos.RunGold(pedido.Won, oleada);
        await cuentas.AddGoldAsync(idCuenta.Value, oroGanado, ct);

        var cuenta = await cuentas.FindByIdAsync(idCuenta.Value, ct);

        return Results.Ok(new FinishResponse(
            oroGanado, cuenta?.Gold ?? 0, oleada, pedido.Won));
    }
}
