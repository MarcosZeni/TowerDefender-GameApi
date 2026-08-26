using TowerOfArgentum.Api.Config;
using TowerOfArgentum.Api.Data;
using TowerOfArgentum.Api.Security;

namespace TowerOfArgentum.Api.Endpoints;

public sealed record PedidoRegistro(string Nick, string Password, string? Email);

public sealed record PedidoLogin(string Nick, string Password);

public sealed record RespuestaSesion(string Token, string Nick, long Oro, int ExpiraEnSegundos);

/// <summary>
/// <c>/auth/registro</c>, <c>/auth/login</c>, <c>/auth/logout</c>.
/// </summary>
public static class AuthEndpoints
{
    private const int NickMinimo = 3;
    private const int NickMaximo = 20;
    private const int PasswordMinima = 8;

    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/auth/registro", RegistrarAsync);
        app.MapPost("/auth/login", LoginAsync);
        app.MapPost("/auth/logout", LogoutAsync);
    }

    private static async Task<IResult> RegistrarAsync(
        PedidoRegistro pedido,
        HttpContext http,
        CuentaRepositorio cuentas,
        TokenRepositorio tokens,
        PasswordHasher hasher,
        HashGate gate,
        ConfigSeguridad seguridad,
        CancellationToken ct)
    {
        string nick = (pedido.Nick ?? "").Trim();

        if (nick.Length is < NickMinimo or > NickMaximo || nick.Contains(' ', StringComparison.Ordinal))
            return Results.BadRequest(ErrorResponse.NickInvalido);

        if ((pedido.Password ?? "").Length < PasswordMinima)
            return Results.BadRequest(ErrorResponse.PasswordInvalida);

        // El hash pasa por el portón: reserva 19 MiB mientras calcula, y sin tope
        // una avalancha de registros pediría toda la memoria de la máquina.
        var (turno, phc) = await gate.TryRunAsync(() => hasher.Hash(pedido.Password!), ct);

        if (!turno)
            return Results.Json(ErrorResponse.Sobrecargado, statusCode: StatusCodes.Status503ServiceUnavailable);

        var ip = http.Connection.RemoteIpAddress;
        long? id = await cuentas.CrearAsync(nick, phc, pedido.Email, ip, ct);

        // null = el UNIQUE rebotó. Se apoya en la base y no en un SELECT previo:
        // entre el chequeo y el insert hay una ventana donde otro request toma el
        // mismo nombre.
        if (id is null)
            return Results.Conflict(ErrorResponse.NickEnUso);

        var (plano, hash) = LoginToken.Create();
        await tokens.GuardarAsync(hash, id.Value, seguridad.TokenTtlSegundos, ip, ct);

        return Results.Ok(new RespuestaSesion(plano, nick, 0, seguridad.TokenTtlSegundos));
    }

    private static async Task<IResult> LoginAsync(
        PedidoLogin pedido,
        HttpContext http,
        CuentaRepositorio cuentas,
        TokenRepositorio tokens,
        IntentoRepositorio intentos,
        PasswordHasher hasher,
        HashGate gate,
        ConfigSeguridad seguridad,
        CancellationToken ct)
    {
        string nick = (pedido.Nick ?? "").Trim();
        string password = pedido.Password ?? "";
        var ipCliente = http.Connection.RemoteIpAddress;

        // ⚠ El freno por NICK es lo único que ve una botnet repartida: mil IPs
        // con un intento cada una, ninguna llega a su límite por IP, y todas
        // apuntan a la misma cuenta.
        //
        // Cuenta solo fallos DESDE EL ÚLTIMO ACIERTO. Si contara todos, cualquiera
        // podría dejar afuera a otro tirándole contraseñas mal a su nombre.
        if (seguridad.MaxFallosPorNick > 0 && nick.Length > 0)
        {
            int fallos = await intentos.FallosDeNickAsync(nick, seguridad.VentanaNickMinutos, ct);

            if (fallos >= seguridad.MaxFallosPorNick)
            {
                await intentos.RegistrarAsync(ipCliente, nick, exito: false, ct);
                return Results.Json(ErrorResponse.DemasiadosIntentos, statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        var cuenta = await cuentas.BuscarPorNickAsync(nick, ct);

        // ⚠ Si el nick no existe se gasta el mismo tiempo igual. Sin esto,
        // "usuario inexistente" contestaría al instante y "contraseña incorrecta"
        // tardaría 40 ms, y cualquiera podría averiguar qué cuentas existen
        // midiendo el tiempo de respuesta.
        if (cuenta is null)
        {
            await gate.TryRunAsync(() => { hasher.BurnTime(password); return 0; }, ct);
            await intentos.RegistrarAsync(ipCliente, nick, exito: false, ct);

            return Results.Json(ErrorResponse.CredencialesInvalidas, statusCode: StatusCodes.Status401Unauthorized);
        }

        var (turno, resultado) = await gate.TryRunAsync(
            () => hasher.Verify(password, cuenta.Password), ct);

        if (!turno)
            return Results.Json(ErrorResponse.Sobrecargado, statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!resultado.Ok)
        {
            await intentos.RegistrarAsync(ipCliente, nick, exito: false, ct);
            return Results.Json(ErrorResponse.CredencialesInvalidas, statusCode: StatusCodes.Status401Unauthorized);
        }

        // Los parámetros de Argon2id van adentro del hash, así que subir el costo
        // no invalida nada: cada contraseña se re-hashea sola en su próximo login.
        if (resultado.NeedsRehash)
        {
            var (ok, nuevo) = await gate.TryRunAsync(() => hasher.Hash(password), ct);
            if (ok)
                await cuentas.ActualizarPasswordAsync(cuenta.Id, nuevo, ct);
        }

        var (plano, hash) = LoginToken.Create();

        await tokens.GuardarAsync(hash, cuenta.Id, seguridad.TokenTtlSegundos, ipCliente, ct);
        await cuentas.MarcarAccesoAsync(cuenta.Id, ipCliente, ct);

        // El acierto reinicia la cuenta de fallos del nick: la consulta de arriba
        // solo mira lo que pasó DESPUÉS del último éxito.
        await intentos.RegistrarAsync(ipCliente, nick, exito: true, ct);

        // El barrido de vencidos corre desde acá, sin job aparte: es una tabla que
        // crece con cada sesión y sin esto no para nunca. De a mil por login.
        await tokens.BarrerVencidosAsync(ct);
        await intentos.BarrerAsync(90, ct);

        return Results.Ok(new RespuestaSesion(plano, cuenta.Nick, cuenta.Oro, seguridad.TokenTtlSegundos));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http,
        TokenRepositorio tokens,
        CancellationToken ct)
    {
        string? plano = Autenticacion.TokenDe(http);
        byte[]? hash = plano is null ? null : LoginToken.HashOf(plano);

        // Se contesta 204 aunque el token no valiera. Un logout que falla no le
        // sirve a nadie, y distinguir "no estabas logueado" de "listo" solo le
        // dice a un atacante si el token que probó existía.
        if (hash is not null)
            await tokens.BorrarAsync(hash, ct);

        return Results.NoContent();
    }
}
