using MySqlConnector;
using TowerOfArgentum.Api.Config;
using TowerOfArgentum.Api.Data;
using TowerOfArgentum.Api.Security;

namespace TowerOfArgentum.Api.Endpoints;

public sealed record RespuestaPerfil(string Nick, long Oro, string[] Planos);

public sealed record PedidoPlano(string Plano);

public sealed record RespuestaCompraPlano(string Plano, int Precio, long OroRestante);

/// <summary>
/// <c>/perfil/me</c>, <c>/ranking</c> y <c>/tienda/plano</c>.
/// </summary>
public static class PerfilEndpoints
{
    public static void MapPerfil(this WebApplication app)
    {
        app.MapGet("/perfil/me", PerfilAsync);
        app.MapGet("/ranking", RankingAsync);
        app.MapPost("/tienda/plano", ComprarPlanoAsync);
    }

    private static async Task<IResult> PerfilAsync(
        HttpContext http,
        TokenRepositorio tokens,
        CuentaRepositorio cuentas,
        Database db,
        CancellationToken ct)
    {
        long? idCuenta = await Autenticacion.CuentaDeAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.NoAutorizado, statusCode: StatusCodes.Status401Unauthorized);

        var cuenta = await cuentas.BuscarPorIdAsync(idCuenta.Value, ct);
        if (cuenta is null)
            return Results.Json(ErrorResponse.NoAutorizado, statusCode: StatusCodes.Status401Unauthorized);

        var planos = new List<string>();

        await using (var conn = await db.OpenAsync(ct))
        await using (var cmd = new MySqlCommand(
            "SELECT plano FROM planos_cuenta WHERE id_cuenta = @cuenta ORDER BY comprado", conn))
        {
            cmd.Parameters.AddWithValue("@cuenta", idCuenta.Value);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                planos.Add(r.GetString(0));
        }

        return Results.Ok(new RespuestaPerfil(cuenta.Nick, cuenta.Oro, [.. planos]));
    }

    /// <summary>
    /// La tabla global.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Sin autenticación y sin filtro de mapa.</b> Es pública a propósito —
    /// mirar el ranking antes de registrarse es parte de lo que hace querer
    /// jugar— y el área no se elige, así que no hay nada por lo que filtrar.
    /// </remarks>
    private static async Task<IResult> RankingAsync(
        RunRepositorio runs,
        ApiConfig config,
        CancellationToken ct,
        int? version = null,
        int limite = 100)
    {
        // La temporada por defecto es la actual. Las viejas siguen consultables
        // pasando ?version=N: cada rebalanceo es una temporada, y borrar las
        // anteriores sería borrar los récords de la gente.
        int datosVersion = version ?? config.DatosVersion;

        var filas = await runs.RankingAsync(datosVersion, Math.Clamp(limite, 1, 500), ct);

        return Results.Ok(new { version = datosVersion, filas });
    }

    /// <summary>
    /// Compra un plano de fusión con la billetera.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>El cliente manda solo el nombre.</b> El precio sale de la lista
    /// del servidor: si lo mandara el cliente, se compraría todo a un peso.</para>
    ///
    /// <para>⚠ <b>El descuento y el alta van en UNA transacción.</b> Sin eso, dos
    /// compras simultáneas leen el mismo saldo y las dos pasan — el clásico
    /// "compré dos planos con el oro de uno". El <c>UPDATE</c> condicional del
    /// saldo cierra esa carrera: si otro request se llevó el oro primero, este
    /// afecta cero filas y la transacción se deshace.</para>
    /// </remarks>
    private static async Task<IResult> ComprarPlanoAsync(
        PedidoPlano pedido,
        HttpContext http,
        TokenRepositorio tokens,
        Database db,
        CancellationToken ct)
    {
        long? idCuenta = await Autenticacion.CuentaDeAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.NoAutorizado, statusCode: StatusCodes.Status401Unauthorized);

        int? precio = CatalogoPlanos.PrecioDe(pedido.Plano);
        if (precio is null)
            return Results.NotFound(ErrorResponse.PlanoDesconocido);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // El descuento y el chequeo de saldo son la misma operación: la condición
        // `oro >= @precio` va en el WHERE, así que la base decide y no hay ventana
        // entre leer y escribir.
        int filas;

        await using (var descontar = new MySqlCommand(
            "UPDATE cuentas SET oro = oro - @precio WHERE id = @cuenta AND oro >= @precio",
            conn, tx))
        {
            descontar.Parameters.AddWithValue("@cuenta", idCuenta.Value);
            descontar.Parameters.AddWithValue("@precio", precio.Value);
            filas = await descontar.ExecuteNonQueryAsync(ct);
        }

        if (filas == 0)
        {
            await tx.RollbackAsync(ct);
            return Results.BadRequest(ErrorResponse.OroInsuficiente);
        }

        try
        {
            await using var alta = new MySqlCommand(
                """
                INSERT INTO planos_cuenta (id_cuenta, plano, precio, comprado)
                VALUES (@cuenta, @plano, @precio, UTC_TIMESTAMP(3))
                """, conn, tx);

            alta.Parameters.AddWithValue("@cuenta", idCuenta.Value);
            alta.Parameters.AddWithValue("@plano", pedido.Plano);
            alta.Parameters.AddWithValue("@precio", precio.Value);

            await alta.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Ya lo tenía. La PK compuesta lo rebota sola, y el rollback devuelve
            // el oro: sin la transacción, comprar dos veces cobraría dos veces.
            await tx.RollbackAsync(ct);
            return Results.Conflict(ErrorResponse.PlanoYaAprendido);
        }

        long restante;

        await using (var leer = new MySqlCommand(
            "SELECT oro FROM cuentas WHERE id = @cuenta", conn, tx))
        {
            leer.Parameters.AddWithValue("@cuenta", idCuenta.Value);
            restante = Convert.ToInt64(await leer.ExecuteScalarAsync(ct));
        }

        await tx.CommitAsync(ct);

        return Results.Ok(new RespuestaCompraPlano(pedido.Plano, precio.Value, restante));
    }
}

/// <summary>
/// Los planos que se venden, con su precio.
/// </summary>
/// <remarks>
/// <para>⚠ <b>La lista vive del lado del servidor y es finita.</b> Que sea finita
/// es la condición que hace tolerable la ventaja del veterano: tiene techo. Por
/// eso no se agrega equipo permanente ni nada que escale sin fin.</para>
///
/// <para>Su casa final es <c>planos.json</c> del servidor, junto al resto de los
/// datos de balance. Acá como constantes mientras no exista el cargador.</para>
/// </remarks>
public static class CatalogoPlanos
{
    private static readonly Dictionary<string, int> _precios = new(StringComparer.Ordinal)
    {
        // "Perforación" no está: es la receta inicial, conocida sin comprar. Sin
        // al menos una, un jugador nuevo nunca ve un Único y la mecánica de fusión
        // le resulta invisible.
        ["Onda de Frío"] = 4000,
    };

    public static int? PrecioDe(string? plano)
        => plano is not null && _precios.TryGetValue(plano, out int precio) ? precio : null;
}
