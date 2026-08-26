using System.Net;
using MySqlConnector;

namespace TowerOfArgentum.Api.Data;

/// <summary>
/// Consultas sobre <c>tokens</c>.
/// </summary>
/// <remarks>
/// ⚠ <b>La parte de canje hay que escribirla de cero.</b> El
/// <c>LoginServer</c> de Austral <b>emite</b> tokens y nunca los canjea —lo hace
/// el server de juego, que es otro proceso—. Acá esta misma API los valida en
/// cada request, así que ese código no existe allá para copiar.
/// </remarks>
public sealed class TokenRepositorio(Database db)
{
    public async Task GuardarAsync(byte[] tokenHash, long idCuenta, int ttlSegundos, IPAddress? ip, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        // ⚠ Las dos fechas las calcula MySQL, no C#. Con dos procesos o dos zonas
        // horarias, comparar una fecha escrita por la aplicación contra
        // UTC_TIMESTAMP() en la consulta da resultados absurdos, y el síntoma es
        // "el token vence al instante" o "no vence nunca".
        await using var cmd = new MySqlCommand(
            """
            INSERT INTO tokens (token_hash, id_cuenta, creado, expira, ip)
            VALUES (@hash, @cuenta, UTC_TIMESTAMP(3),
                    DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @ttl SECOND), @ip)
            """, conn);

        cmd.Parameters.AddWithValue("@hash", tokenHash);
        cmd.Parameters.AddWithValue("@cuenta", idCuenta);
        cmd.Parameters.AddWithValue("@ttl", ttlSegundos);
        cmd.Parameters.AddWithValue("@ip", (object?)CuentaRepositorio.Normalizar(ip) ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Canjea un token: devuelve la cuenta si vale, <c>null</c> si no.
    /// </summary>
    /// <remarks>
    /// El chequeo de vencimiento va <b>en el WHERE</b> y no en C#. Que lo compare
    /// la base es lo que garantiza que las dos puntas —el <c>expira</c> escrito y
    /// el "ahora" con el que se compara— salgan del mismo reloj.
    /// </remarks>
    public async Task<long?> CanjearAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            SELECT id_cuenta FROM tokens
            WHERE token_hash = @hash AND expira > UTC_TIMESTAMP(3)
            LIMIT 1
            """, conn);

        cmd.Parameters.AddWithValue("@hash", tokenHash);

        object? resultado = await cmd.ExecuteScalarAsync(ct);
        return resultado is null or DBNull ? null : Convert.ToInt64(resultado);
    }

    public async Task BorrarAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new MySqlCommand("DELETE FROM tokens WHERE token_hash = @hash", conn);

        cmd.Parameters.AddWithValue("@hash", tokenHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Barre los vencidos.
    /// </summary>
    /// <remarks>
    /// Corre desde el propio camino del login, sin job aparte: es una tabla que
    /// crece con cada sesión y sin barrido no para nunca. El <c>LIMIT</c> evita
    /// que un barrido atrasado bloquee la tabla por un rato largo — se van
    /// llevando de a mil por login.
    /// </remarks>
    public async Task<int> BarrerVencidosAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            "DELETE FROM tokens WHERE expira < UTC_TIMESTAMP(3) LIMIT 1000", conn);

        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
