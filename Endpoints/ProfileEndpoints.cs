using System.Text.Json.Serialization;
using MySqlConnector;
using TowerOfArgentum.Api.Config;
using TowerOfArgentum.Api.Data;
using TowerOfArgentum.Api.Security;

namespace TowerOfArgentum.Api.Endpoints;

// ⚠ LOS [JsonPropertyName] SON EL CONTRATO, no decoración. Las propiedades van
//   en inglés como todo el código; los nombres que viajan por el cable siguen en
//   español porque el cliente ya está escrito contra ellos y los tres repos se
//   despliegan por separado. Sacar un atributo renombra un campo de la API sin
//   que nada falle al compilar: el cliente recibiría el campo en cero.
public sealed record ProfileResponse(
    string Nick,
    [property: JsonPropertyName("oro")] long Gold,
    [property: JsonPropertyName("planos")] string[] Blueprints);

public sealed record BlueprintRequest(
    [property: JsonPropertyName("plano")] string Blueprint);

public sealed record BlueprintPurchaseResponse(
    [property: JsonPropertyName("plano")] string Blueprint,
    [property: JsonPropertyName("precio")] int Price,
    [property: JsonPropertyName("oroRestante")] long RemainingGold);

/// <summary>Un plano a la venta, con su precio.</summary>
public sealed record BlueprintRow(
    [property: JsonPropertyName("plano")] string Blueprint,
    [property: JsonPropertyName("precio")] int Price);

public sealed record BlueprintsResponse(
    [property: JsonPropertyName("planos")] BlueprintRow[] Blueprints);

/// <summary>
/// <c>/perfil/me</c>, <c>/ranking</c>, <c>/tienda/planos</c> y <c>/tienda/plano</c>.
/// </summary>
public static class ProfileEndpoints
{
    public static void MapProfile(this WebApplication app)
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
    private static IResult ListarPlanos(ServerData datos)
    {
        var filas = new List<BlueprintRow>();

        foreach (var plano in datos.Blueprints)
            filas.Add(new BlueprintRow(plano.Key, plano.Price));

        return Results.Ok(new BlueprintsResponse([.. filas]));
    }

    private static async Task<IResult> PerfilAsync(
        HttpContext http,
        TokenRepository tokens,
        AccountRepository cuentas,
        Database db,
        CancellationToken ct)
    {
        long? idCuenta = await Authentication.AccountOfAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.Unauthorized, statusCode: StatusCodes.Status401Unauthorized);

        var cuenta = await cuentas.FindByIdAsync(idCuenta.Value, ct);
        if (cuenta is null)
            return Results.Json(ErrorResponse.Unauthorized, statusCode: StatusCodes.Status401Unauthorized);

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

        return Results.Ok(new ProfileResponse(cuenta.Nick, cuenta.Gold, [.. planos]));
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
        RunRepository runs,
        ApiConfig config,
        SecurityConfig seguridad,
        CancellationToken ct,
        int? version = null,
        int limite = 100)
    {
        // La temporada por defecto es la actual. Las viejas siguen consultables
        // pasando ?version=N: cada rebalanceo es una temporada, y borrar las
        // anteriores sería borrar los récords de la gente.
        int datosVersion = version ?? config.DataVersion;

        var filas = await runs.RankingAsync(
            datosVersion, Math.Clamp(limite, 1, seguridad.MaxRankingRows), ct);

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
        BlueprintRequest pedido,
        HttpContext http,
        TokenRepository tokens,
        Database db,
        ServerData datos,
        CancellationToken ct)
    {
        long? idCuenta = await Authentication.AccountOfAsync(http, tokens, ct);
        if (idCuenta is null)
            return Results.Json(ErrorResponse.Unauthorized, statusCode: StatusCodes.Status401Unauthorized);

        int? precio = datos.BlueprintPrice(pedido.Blueprint);
        if (precio is null)
            return Results.NotFound(ErrorResponse.UnknownBlueprint);

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
            return Results.BadRequest(ErrorResponse.NotEnoughGold);
        }

        try
        {
            await using var alta = new MySqlCommand(
                """
                INSERT INTO planos_cuenta (id_cuenta, plano, precio, comprado)
                VALUES (@cuenta, @plano, @precio, UTC_TIMESTAMP(3))
                """, conn, tx);

            alta.Parameters.AddWithValue("@cuenta", idCuenta.Value);
            alta.Parameters.AddWithValue("@plano", pedido.Blueprint);
            alta.Parameters.AddWithValue("@precio", precio.Value);

            await alta.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Ya lo tenía. La PK compuesta lo rebota sola, y el rollback devuelve
            // el oro: sin la transacción, comprar dos veces cobraría dos veces.
            await tx.RollbackAsync(ct);
            return Results.Conflict(ErrorResponse.BlueprintAlreadyLearned);
        }

        long restante;

        await using (var leer = new MySqlCommand(
            "SELECT oro FROM cuentas WHERE id = @cuenta", conn, tx))
        {
            leer.Parameters.AddWithValue("@cuenta", idCuenta.Value);
            restante = Convert.ToInt64(await leer.ExecuteScalarAsync(ct));
        }

        await tx.CommitAsync(ct);

        return Results.Ok(new BlueprintPurchaseResponse(pedido.Blueprint, precio.Value, restante));
    }
}
