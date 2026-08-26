using System.Net;
using MySqlConnector;

namespace TowerOfArgentum.Api.Data;

/// <summary>Lo que hay que saber de una cuenta para atender un request.</summary>
public sealed record Cuenta(long Id, string Nick, string Password, long Oro);

/// <summary>
/// Consultas sobre <c>cuentas</c>.
/// </summary>
/// <remarks>
/// SQL en raw string con <c>AddWithValue</c>, sin ORM. Es el estilo del
/// LoginServer y se mantiene a propósito: son cuatro tablas y las consultas son
/// las que son. Un ORM acá agregaría una capa que hay que aprender para leer un
/// <c>SELECT</c> de tres líneas.
/// </remarks>
public sealed class CuentaRepositorio(Database db)
{
    public async Task<Cuenta?> BuscarPorNickAsync(string nick, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        // La colación de la base es _ai_ci, así que esta comparación ya ignora
        // mayúsculas y acentos: no hace falta LOWER() —que además invalidaría el
        // índice y forzaría un scan de tabla.
        await using var cmd = new MySqlCommand(
            "SELECT id, nick, password, oro FROM cuentas WHERE nick = @nick LIMIT 1", conn);

        cmd.Parameters.AddWithValue("@nick", nick);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return new Cuenta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3));
    }

    public async Task<Cuenta?> BuscarPorIdAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            "SELECT id, nick, password, oro FROM cuentas WHERE id = @id LIMIT 1", conn);

        cmd.Parameters.AddWithValue("@id", id);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return new Cuenta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3));
    }

    /// <summary>
    /// Crea la cuenta. Devuelve <c>null</c> si el nick ya estaba tomado.
    /// </summary>
    /// <remarks>
    /// Se apoya en el <c>UNIQUE</c> del nick en vez de hacer un <c>SELECT</c>
    /// previo: entre el chequeo y el insert hay una ventana donde otro request
    /// puede tomar el mismo nombre, y bajo registro simultáneo esa ventana se
    /// abre de verdad. La base es el único árbitro que no tiene carrera.
    /// </remarks>
    public async Task<long?> CrearAsync(string nick, string passwordPhc, string? email, IPAddress? ip, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            INSERT INTO cuentas (nick, password, email, oro, ultima_ip, creada)
            VALUES (@nick, @password, @email, 0, @ip, UTC_TIMESTAMP(3))
            """, conn);

        cmd.Parameters.AddWithValue("@nick", nick);
        cmd.Parameters.AddWithValue("@password", passwordPhc);
        cmd.Parameters.AddWithValue("@email", (object?)email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ip", (object?)Normalizar(ip) ?? DBNull.Value);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return cmd.LastInsertedId;
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return null;
        }
    }

    public async Task MarcarAccesoAsync(long idCuenta, IPAddress? ip, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            "UPDATE cuentas SET ultimo_acceso = UTC_TIMESTAMP(3), ultima_ip = @ip WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("@id", idCuenta);
        cmd.Parameters.AddWithValue("@ip", (object?)Normalizar(ip) ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ActualizarPasswordAsync(long idCuenta, string passwordPhc, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            "UPDATE cuentas SET password = @password WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("@id", idCuenta);
        cmd.Parameters.AddWithValue("@password", passwordPhc);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Suma oro a la billetera.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Lo llama solo el servidor, con un número que calculó el servidor.</b>
    /// El cliente nunca manda oro: <c>/run/finish</c> reporta hasta dónde llegó y
    /// acá se deriva. Oro falso compra todos los planos y se vuelve poder real, y
    /// la columna <c>validado</c> no sirve de freno porque el oro se gasta al
    /// instante.
    /// </remarks>
    public async Task SumarOroAsync(long idCuenta, long oro, CancellationToken ct)
    {
        if (oro <= 0)
            return;

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            "UPDATE cuentas SET oro = oro + @oro WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("@id", idCuenta);
        cmd.Parameters.AddWithValue("@oro", oro);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>IPv4 mapeado a 4 bytes, IPv6 a 16. Es lo que espera VARBINARY(16).</summary>
    public static byte[]? Normalizar(IPAddress? ip)
    {
        if (ip is null)
            return null;

        // Un IPv4 llega a veces como ::ffff:1.2.3.4 según cómo esté el socket.
        // Sin esto, la misma IP se guardaría de dos formas distintas y cualquier
        // conteo por IP contaría doble.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        return ip.GetAddressBytes();
    }
}
