using System.Security.Cryptography;
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
public sealed record PedidoStart(string HashDatos);

public sealed record RespuestaStart(long RunId, ulong Seed, int DatosVersion, long? Abandonada);

/// <summary>
/// Lo que reclama el cliente al terminar.
/// </summary>
/// <remarks>
/// ⚠ <b>No lleva oro ni precios.</b> El servidor deriva
/// <c>oroGanado = f(gano, oleada)</c>. Si el cliente mandara el oro, oro falso
/// compraría todos los planos y se volvería poder real — y la columna
/// <c>validado</c> no serviría de freno, porque el oro se gasta al instante.
/// </remarks>
public sealed record PedidoFinish(
    long RunId, bool Gano, int Oleada, int Area, int Ticks, string? Log);

public sealed record RespuestaFinish(long OroGanado, long OroTotal, int Oleada, bool Gano);

public static class RunEndpoints
{
    /// <summary>Tope del log comprimido. Más que esto entra truncado y se marca.</summary>
    private const int MaxLogBytes = 8 * 1024 * 1024;

    public static void MapRuns(this WebApplication app)
    {
        app.MapPost("/run/start", StartAsync);
        app.MapPost("/run/finish", FinishAsync);
    }

    private static async Task<IResult> StartAsync(
        PedidoStart pedido,
        HttpContext http,
        TokenRepositorio tokens,
        RunRepositorio runs,
        ApiConfig config,
        CancellationToken ct)
    {
        long? idCuenta = await Autenticacion.CuentaDeAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.NoAutorizado, statusCode: StatusCodes.Status401Unauthorized);

        // ⚠ El hash de los datos de balance se verifica ACÁ, al abrir. Si el
        // cliente juega con otros números que el servidor, la corrida no es
        // reproducible y el ranking deja de comparar lo mismo. Rechazarla al
        // empezar es infinitamente mejor que rechazarla después de tres horas.
        if (!string.IsNullOrEmpty(config.HashDatos) &&
            !string.Equals(pedido.HashDatos, config.HashDatos, StringComparison.Ordinal))
        {
            return Results.Json(ErrorResponse.DatosDesactualizados, statusCode: StatusCodes.Status409Conflict);
        }

        // ⚠ La seed la genera el SERVIDOR, con CSPRNG. Si la eligiera el cliente,
        // podría probar semillas hasta encontrar una fácil y reclamar la corrida
        // como buena.
        ulong seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

        var (idNueva, abandonada) = await runs.AbrirAsync(idCuenta.Value, seed, config.DatosVersion, ct);

        return Results.Ok(new RespuestaStart(idNueva, seed, config.DatosVersion, abandonada));
    }

    private static async Task<IResult> FinishAsync(
        PedidoFinish pedido,
        HttpContext http,
        TokenRepositorio tokens,
        RunRepositorio runs,
        CuentaRepositorio cuentas,
        CancellationToken ct)
    {
        long? idCuenta = await Autenticacion.CuentaDeAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.NoAutorizado, statusCode: StatusCodes.Status401Unauthorized);

        var run = await runs.BuscarAbiertaAsync(pedido.RunId, idCuenta.Value, ct);

        // Cubre las tres cosas de una: no existe, no es tuya, o ya está cerrada.
        // No se distinguen a propósito: decir "existe pero no es tuya" confirma
        // que el id acertó.
        if (run is null)
            return Results.NotFound(ErrorResponse.RunNoEncontrada);

        int oleada = Math.Clamp(pedido.Oleada, 0, 100);
        int area = Math.Clamp(pedido.Area, 1, 8);
        int ticks = Math.Max(0, pedido.Ticks);

        // ⚠ Solo se puede ganar en la oleada 100, matando al jefe final. Un
        // reclamo de "gané en la 40" es imposible por reglas del juego, así que se
        // rechaza entero en vez de corregirse en silencio: si llega uno, o hay un
        // bug del cliente o alguien está probando, y las dos cosas conviene verlas.
        if (pedido.Gano && oleada != 100)
            return Results.BadRequest(ErrorResponse.ReclamoInconsistente);

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

            if (log is not null && log.Length > MaxLogBytes)
            {
                log = log[..MaxLogBytes];
                truncado = true;
            }
        }

        await runs.CerrarAsync(pedido.RunId, pedido.Gano, oleada, area, ticks, log, truncado, ct);

        long oroGanado = CalcularOro(pedido.Gano, oleada);
        await cuentas.SumarOroAsync(idCuenta.Value, oroGanado, ct);

        var cuenta = await cuentas.BuscarPorIdAsync(idCuenta.Value, ct);

        return Results.Ok(new RespuestaFinish(
            oroGanado, cuenta?.Oro ?? 0, oleada, pedido.Gano));
    }

    /// <summary>
    /// El oro que va a la billetera. <b>Lo calcula el servidor, siempre.</b>
    /// </summary>
    /// <remarks>
    /// <para>Cuadrático en la oleada, no lineal: llegar a la 80 tiene que valer
    /// bastante más que el doble de llegar a la 40, o no hay incentivo para
    /// mejorar una vez que se llegó a un lugar cómodo.</para>
    ///
    /// <para>El bonus por ganar es grande y aparte: es el objetivo del juego.</para>
    ///
    /// <para>⚠ Esta plata compra planos, que son progresión permanente. Es el
    /// número más goloso de todo el sistema para un tramposo, y por eso no toca
    /// nunca nada que venga del cliente salvo <c>oleada</c>, que está acotada a
    /// 0..100 más arriba.</para>
    /// </remarks>
    private static long CalcularOro(bool gano, int oleada)
    {
        long baseOro = (long)oleada * oleada / 2;
        return gano ? baseOro + 5000 : baseOro;
    }
}
