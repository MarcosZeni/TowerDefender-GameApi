using System.Net;
using MySqlConnector;

namespace TowerOfArgentum.Api.Data;

/// <summary>
/// El freno que se acuerda: cuenta intentos de login sobre la base.
/// </summary>
/// <remarks>
/// <para>Va <b>junto</b> con el rate limiter en memoria de Kestrel, no en su
/// lugar. Ninguno de los dos alcanza solo:</para>
///
/// <list type="bullet">
/// <item>El de memoria es instantáneo y no toca la base, que es lo que hace falta
/// bajo una avalancha — pero se olvida de todo al reiniciar, y con dos instancias
/// cada una cuenta por su lado.</item>
/// <item>Este se acuerda y ve a través de los reinicios, pero cuesta una consulta
/// por pedido: sacar el de memoria convertiría cada request de una avalancha en
/// una consulta a MySQL.</item>
/// </list>
///
/// <para>Y el conteo <b>por nick</b> es lo único que ve una botnet repartida:
/// mil IPs con un intento cada una, ninguna llega a su límite por IP, y todas
/// apuntan a la misma cuenta.</para>
/// </remarks>
public sealed class LoginAttemptRepository(Database db)
{
    public async Task RegisterAsync(IPAddress? ip, string? nick, bool exito, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            INSERT INTO intentos_login (ip, nick, exito, cuando)
            VALUES (@ip, @nick, @exito, UTC_TIMESTAMP(3))
            """, conn);

        cmd.Parameters.AddWithValue("@ip", (object?)AccountRepository.Normalize(ip) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nick", (object?)nick ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@exito", exito ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Intentos de esa IP en el último minuto.</summary>
    public async Task<int> IpAttemptsAsync(IPAddress? ip, CancellationToken ct)
    {
        byte[]? bytes = AccountRepository.Normalize(ip);

        if (bytes is null)
            return 0;

        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            SELECT COUNT(*) FROM intentos_login
            WHERE ip = @ip AND cuando > DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 MINUTE)
            """, conn);

        cmd.Parameters.AddWithValue("@ip", bytes);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Fallos seguidos contra ese nick dentro de la ventana.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Solo cuenta desde el último acierto.</b> Un login correcto reinicia
    /// la cuenta; si contara todos los fallos de la ventana, cualquiera podría
    /// dejar afuera a otro tirándole contraseñas mal a su nombre — y el dueño
    /// legítimo quedaría bloqueado sin haber hecho nada.
    /// </remarks>
    public async Task<int> NickFailuresAsync(string nick, int ventanaMinutos, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            SELECT COUNT(*) FROM intentos_login
            WHERE nick = @nick
              AND exito = 0
              AND cuando > DATE_SUB(UTC_TIMESTAMP(3), INTERVAL @ventana MINUTE)
              AND cuando > COALESCE((
                    SELECT MAX(cuando) FROM intentos_login
                    WHERE nick = @nick AND exito = 1
              ), '1970-01-01')
            """, conn);

        cmd.Parameters.AddWithValue("@nick", nick);
        cmd.Parameters.AddWithValue("@ventana", ventanaMinutos);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Borra los intentos viejos.
    /// </summary>
    /// <remarks>
    /// Corre desde el camino del login, sin job aparte: se escribe una fila por
    /// intento y bajo abuso son muchas por segundo, así que sin barrido la tabla
    /// crece para siempre. El <c>LIMIT</c> evita que un barrido atrasado bloquee
    /// la tabla por un rato largo.
    /// </remarks>
    public async Task<int> SweepAsync(int diasRetencion, CancellationToken ct)
    {
        if (diasRetencion <= 0)
            return 0;

        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            "DELETE FROM intentos_login WHERE cuando < DATE_SUB(UTC_TIMESTAMP(3), INTERVAL @dias DAY) LIMIT 1000",
            conn);

        cmd.Parameters.AddWithValue("@dias", diasRetencion);

        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
