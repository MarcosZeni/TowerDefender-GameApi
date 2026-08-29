using System.Text.Json.Serialization;
using TowerOfArgentum.Api.Config;
using TowerOfArgentum.Api.Data;
using TowerOfArgentum.Api.Security;

namespace TowerOfArgentum.Api.Endpoints;

public sealed record RegisterRequest(string Nick, string Password, string? Email);

public sealed record LoginRequest(string Nick, string Password);

// ⚠ LOS [JsonPropertyName] SON EL CONTRATO, no decoración. Las propiedades van
//   en inglés como todo el código; los nombres que viajan por el cable siguen en
//   español porque el cliente ya está escrito contra ellos y los tres repos se
//   despliegan por separado. Sacar un atributo renombra un campo de la API sin
//   que nada falle al compilar: el cliente recibiría el campo en cero.
public sealed record SessionResponse(
    string Token,
    string Nick,
    [property: JsonPropertyName("oro")] long Gold,
    [property: JsonPropertyName("expiraEnSegundos")] int ExpiresInSeconds);

/// <summary>
/// <c>/auth/registro</c>, <c>/auth/login</c>, <c>/auth/logout</c>.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/auth/registro", RegisterAsync);
        app.MapPost("/auth/login", LoginAsync);
        app.MapPost("/auth/logout", LogoutAsync);
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest pedido,
        HttpContext http,
        AccountRepository cuentas,
        TokenRepository tokens,
        PasswordHasher hasher,
        HashGate gate,
        SecurityConfig seguridad,
        CancellationToken ct)
    {
        string nick = (pedido.Nick ?? "").Trim();

        if (nick.Length < seguridad.MinNick || nick.Length > seguridad.MaxNick ||
            nick.Contains(' ', StringComparison.Ordinal))
            return Results.BadRequest(ErrorResponse.BadNick);

        if ((pedido.Password ?? "").Length < seguridad.MinPassword)
            return Results.BadRequest(ErrorResponse.BadPassword);

        // El hash pasa por el portón: reserva 19 MiB mientras calcula, y sin tope
        // una avalancha de registros pediría toda la memoria de la máquina.
        var (turno, phc) = await gate.TryRunAsync(() => hasher.Hash(pedido.Password!), ct);

        if (!turno)
            return Results.Json(ErrorResponse.Busy, statusCode: StatusCodes.Status503ServiceUnavailable);

        var ip = http.Connection.RemoteIpAddress;
        long? id = await cuentas.CreateAsync(nick, phc, pedido.Email, ip, ct);

        // null = el UNIQUE rebotó. Se apoya en la base y no en un SELECT previo:
        // entre el chequeo y el insert hay una ventana donde otro request toma el
        // mismo nombre.
        if (id is null)
            return Results.Conflict(ErrorResponse.NickTaken);

        var (plano, hash) = LoginToken.Create();
        await tokens.SaveAsync(hash, id.Value, seguridad.TokenTtlSeconds, ip, ct);

        return Results.Ok(new SessionResponse(plano, nick, 0, seguridad.TokenTtlSeconds));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest pedido,
        HttpContext http,
        AccountRepository cuentas,
        TokenRepository tokens,
        LoginAttemptRepository intentos,
        PasswordHasher hasher,
        HashGate gate,
        SecurityConfig seguridad,
        CancellationToken ct)
    {
        string nick = (pedido.Nick ?? "").Trim();
        string password = pedido.Password ?? "";
        var ipCliente = http.Connection.RemoteIpAddress;

        // ⚠ El freno por NICK es lo único que ve una botnet repartida: mil IPs
        // con un intento cada una, ninguna llega a su límite por IP, y todas
        // apuntan a la misma cuenta.
        //
        // Account solo fallos DESDE EL ÚLTIMO ACIERTO. Si contara todos, cualquiera
        // podría dejar afuera a otro tirándole contraseñas mal a su nombre.
        if (seguridad.MaxFailuresPerNick > 0 && nick.Length > 0)
        {
            int fallos = await intentos.NickFailuresAsync(nick, seguridad.NickWindowMinutes, ct);

            if (fallos >= seguridad.MaxFailuresPerNick)
            {
                await intentos.RegisterAsync(ipCliente, nick, exito: false, ct);
                return Results.Json(ErrorResponse.TooManyAttempts, statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        var cuenta = await cuentas.FindByNickAsync(nick, ct);

        // ⚠ Si el nick no existe se gasta el mismo tiempo igual. Sin esto,
        // "usuario inexistente" contestaría al instante y "contraseña incorrecta"
        // tardaría 40 ms, y cualquiera podría averiguar qué cuentas existen
        // midiendo el tiempo de respuesta.
        if (cuenta is null)
        {
            await gate.TryRunAsync(() => { hasher.BurnTime(password); return 0; }, ct);
            await intentos.RegisterAsync(ipCliente, nick, exito: false, ct);

            return Results.Json(ErrorResponse.BadCredentials, statusCode: StatusCodes.Status401Unauthorized);
        }

        var (turno, resultado) = await gate.TryRunAsync(
            () => hasher.Verify(password, cuenta.Password), ct);

        if (!turno)
            return Results.Json(ErrorResponse.Busy, statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!resultado.Ok)
        {
            await intentos.RegisterAsync(ipCliente, nick, exito: false, ct);
            return Results.Json(ErrorResponse.BadCredentials, statusCode: StatusCodes.Status401Unauthorized);
        }

        // Los parámetros de Argon2id van adentro del hash, así que subir el costo
        // no invalida nada: cada contraseña se re-hashea sola en su próximo login.
        if (resultado.NeedsRehash)
        {
            var (ok, nuevo) = await gate.TryRunAsync(() => hasher.Hash(password), ct);
            if (ok)
                await cuentas.UpdatePasswordAsync(cuenta.Id, nuevo, ct);
        }

        var (plano, hash) = LoginToken.Create();

        await tokens.SaveAsync(hash, cuenta.Id, seguridad.TokenTtlSeconds, ipCliente, ct);
        await cuentas.MarkAccessAsync(cuenta.Id, ipCliente, ct);

        // El acierto reinicia la cuenta de fallos del nick: la consulta de arriba
        // solo mira lo que pasó DESPUÉS del último éxito.
        await intentos.RegisterAsync(ipCliente, nick, exito: true, ct);

        // El barrido de vencidos corre desde acá, sin job aparte: es una tabla que
        // crece con cada sesión y sin esto no para nunca. De a mil por login.
        await tokens.SweepExpiredAsync(ct);
        await intentos.SweepAsync(seguridad.LoginAttemptDays, ct);

        return Results.Ok(new SessionResponse(plano, cuenta.Nick, cuenta.Gold, seguridad.TokenTtlSeconds));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http,
        TokenRepository tokens,
        CancellationToken ct)
    {
        string? plano = Authentication.TokenOf(http);
        byte[]? hash = plano is null ? null : LoginToken.HashOf(plano);

        // Se contesta 204 aunque el token no valiera. Un logout que falla no le
        // sirve a nadie, y distinguir "no estabas logueado" de "listo" solo le
        // dice a un atacante si el token que probó existía.
        if (hash is not null)
            await tokens.DeleteAsync(hash, ct);

        return Results.NoContent();
    }
}
