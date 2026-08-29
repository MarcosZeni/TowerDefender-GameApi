using MySqlConnector;
using TowerOfArgentum.Api.Config;
using TowerOfArgentum.Api.Data;
using TowerOfArgentum.Api.Security;

namespace TowerOfArgentum.Api.Endpoints;

public sealed record RespuestaPerfil(string Nick, long Oro, string[] Planos);

public sealed record PedidoPlano(string Plano);

public sealed record RespuestaCompraPlano(string Plano, int Precio, long OroRestante);

/// <summary>Un plano a la venta, con su precio.</summary>
public sealed record FilaPlano(string Plano, int Precio);

public sealed record RespuestaPlanos(FilaPlano[] Planos);

/// <summary>
/// <c>/perfil/me</c>, <c>/ranking</c>, <c>/tienda/planos</c> y <c>/tienda/plano</c>.
/// </summary>
public static class PerfilEndpoints
{
    public static void MapPerfil(this WebApplication app)
    {
        app.MapGet("/perfil/me", PerfilAsync);
        app.MapGet("/ranking", RankingAsync);
        app.MapGet("/tienda/planos", ListarPlanos);
        app.MapPost("/tienda/plano", ComprarPlanoAsync);
    }

    /// <summary>
    /// La lista de precios de la tienda de planos.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>Existe porque el precio vive del lado del servidor y el cliente
    /// no tenía forma de saberlo.</b> La pantalla de la tienda dibujaba nueve
    /// filas con un botón «comprar» y ninguna decía cuánto costaba: el jugador
    /// apretaba para averiguar el precio, y el que no le alcanzaba se enteraba con
    /// un error. Poner el precio en el <c>planos.json</c> del cliente no era una
    /// opción — ese archivo entra al hash de <c>/run/start</c>, así que cambiar un
    /// precio invalidaría la temporada.</para>
    ///
    /// <para><b>Sin token, como el ranking.</b> Los precios no son de nadie, y qué
    /// planos tiene la cuenta ya lo dice <c>/perfil/me</c>: la pantalla cruza las
    /// dos listas. Pedir token acá sería pedirlo para leer una lista de precios
    /// pública.</para>
    ///
    /// <para><b>El plano inicial no está en la lista</b>, porque no se vende. El
    /// cliente lo sabe por su propio <c>planos.json</c> (<c>"initial": true</c>) y
    /// lo dibuja como conocido.</para>
    /// </remarks>
    private static IResult ListarPlanos()
    {
        var filas = new List<FilaPlano>();

        foreach (var (plano, precio) in CatalogoPlanos.Todos)
            filas.Add(new FilaPlano(plano, precio));

        return Results.Ok(new RespuestaPlanos([.. filas]));
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
    // ⚠ La clave es la KEY del plano, no su nombre (cambiado el 2026-08-27).
    //
    // Antes decía "Onda de Frío". El nombre del plano pasó a vivir en los
    // archivos de idioma del cliente y cambia con el idioma: un jugador en inglés
    // habría mandado "Cone of Cold" y el servidor le habría contestado
    // «plano_desconocido». La key —«ondaDeFrio»— es la misma en todos los idiomas
    // y sale de `Datos\perks.json` del cliente.
    //
    // ⚠ La columna `planos_cuenta.plano` guarda esta misma cadena. Las filas
    // escritas antes del cambio tienen el nombre viejo y NO van a matchear: hay
    // que migrarlas o borrarlas.
    //
    // ⚠ ESTÁN LOS OCHO, Y ANTES ESTABA SOLO «ondaDeFrio». La pantalla del cliente
    // lista los nueve planos de su `planos.json` y ofrecía comprar los ocho que no
    // son el inicial: siete de esos ocho botones contestaban «plano_desconocido»,
    // porque acá no había precio. Un catálogo incompleto del lado del servidor no
    // se ve como un catálogo incompleto: se ve como una tienda rota.
    //
    // ⚠ TODOS AL MISMO PRECIO, a propósito. Los nueve Únicos no son mejores unos
    // que otros — son para clases distintas —, así que un precio escalonado
    // inventaría una jerarquía que el balance no tiene y empujaría a comprar «el
    // barato primero» en vez de «el de la clase que juego». La decisión que la
    // tienda tiene que hacer interesante es CUÁL, no en qué orden.
    //
    // El número sale del oro que deja una corrida: `oleada² / 2`, más 5.000 por
    // ganar. Una corrida normal que llega a la 65 deja ~2.100, una ganada deja
    // 10.000. A 4.000 cada uno, los ocho salen 32.000 — del orden de quince
    // corridas buenas o tres ganadas. Es caro a propósito: son la única
    // progresión permanente del proyecto y la lista es finita, así que el techo
    // se alcanza igual; lo único que se elige es cuánto tarda.
    private static readonly Dictionary<string, int> _precios = new(StringComparer.Ordinal)
    {
        // "perforacion" no está: es la receta inicial, conocida sin comprar. Sin
        // al menos una, un jugador nuevo nunca ve un Único y la mecánica de fusión
        // le resulta invisible.
        ["ondaDeFrio"] = 4000,
        ["iraDeDios"] = 4000,
        ["apocalipsis"] = 4000,
        ["terribleHambre"] = 4000,
        ["tormentaDivina"] = 4000,
        ["ceguera"] = 4000,
        ["fuerza"] = 4000,
        ["invisibilidad"] = 4000,
    };

    /// <summary>
    /// Todo lo que se vende, para <c>GET /tienda/planos</c>.
    /// </summary>
    /// <remarks>
    /// El orden no importa: el cliente dibuja en el orden de <b>su</b>
    /// <c>planos.json</c> y busca el precio por key.
    /// </remarks>
    public static IReadOnlyDictionary<string, int> Todos => _precios;

    public static int? PrecioDe(string? plano)
        => plano is not null && _precios.TryGetValue(plano, out int precio) ? precio : null;
}
