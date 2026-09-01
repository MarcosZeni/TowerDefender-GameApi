using System.Text.Json.Serialization;
using MySqlConnector;

namespace TowerOfArgentum.Api.Data;

/// <summary>Una fila del ranking.</summary>
public sealed record RankingRow(
    [property: JsonPropertyName("puesto")] int Position,
    string Nick,
    [property: JsonPropertyName("gano")] bool Won,
    [property: JsonPropertyName("oleada")] int Wave,
    int Area,
    int Ticks,
    [property: JsonPropertyName("validado")] bool Validated);

/// <summary>Una corrida abierta.</summary>
public sealed record OpenRun(long Id, ulong Seed, int DataVersion);

/// <summary>Una corrida abierta con su punto de reanudación.</summary>
/// <remarks>
/// El <c>Log</c> es el mismo blob que escribe <c>SaveFile</c>: con la seed y el
/// tick alcanza para reconstruir el estado exacto re-simulando.
/// </remarks>
public sealed record SavedRun(
    long Id,
    ulong Seed,
    int DataVersion,
    int Wave,
    int Area,
    int Ticks,
    int SavedTick,
    byte[]? Log,
    bool LogTruncated,
    string Platform,
    System.DateTime? UpdatedUtc);

/// <summary>
/// Consultas sobre <c>runs</c>.
/// </summary>
public sealed class RunRepository(Database db)
{
    /// <summary>
    /// Abre una corrida y cierra la anterior como <c>abandono</c>.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>Una sola corrida abierta por cuenta.</b> Es lo que reemplaza al
    /// vencimiento por tiempo: una corrida son 100 oleadas y puede durar horas,
    /// así que un plazo de 90 minutos rechazaría un finish después de tres horas
    /// bien jugadas. Con esta regla no hace falta ningún proceso de limpieza, y
    /// "la cierra el usuario" queda cubierto solo.</para>
    ///
    /// <para>Las dos escrituras van en <b>una transacción</b>: si el cierre
    /// pasara y el alta fallara, la cuenta quedaría sin corrida y con la anterior
    /// perdida.</para>
    /// </remarks>
    public async Task<(long IdNueva, long? IdAbandonada)> AbrirAsync(
        long idCuenta, ulong seed, int datosVersion, string plataforma, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long? abandonada = null;

        await using (var buscar = new MySqlCommand(
            "SELECT id FROM runs WHERE id_cuenta = @cuenta AND motivo_cierre IS NULL LIMIT 1",
            conn, tx))
        {
            buscar.Parameters.AddWithValue("@cuenta", idCuenta);
            object? previa = await buscar.ExecuteScalarAsync(ct);

            if (previa is not null and not DBNull)
                abandonada = Convert.ToInt64(previa);
        }

        if (abandonada is not null)
        {
            await using var cerrar = new MySqlCommand(
                """
                UPDATE runs
                SET motivo_cierre = 'abandono', terminada = UTC_TIMESTAMP(3)
                WHERE id = @id
                """, conn, tx);

            cerrar.Parameters.AddWithValue("@id", abandonada.Value);
            await cerrar.ExecuteNonQueryAsync(ct);
        }

        long idNueva;

        await using (var abrir = new MySqlCommand(
            """
            INSERT INTO runs (id_cuenta, seed, datos_version, plataforma, iniciada)
            VALUES (@cuenta, @seed, @version, @plataforma, UTC_TIMESTAMP(3))
            """, conn, tx))
        {
            abrir.Parameters.AddWithValue("@cuenta", idCuenta);
            abrir.Parameters.AddWithValue("@seed", seed);
            abrir.Parameters.AddWithValue("@version", datosVersion);

            // ⚠ Es una DECLARACIÓN del cliente y no se puede verificar nunca: el
            // área, la oleada y los ticks se re-derivan de la seed más el log; la
            // plataforma no sale de ahí. Se guarda para poder medir si el control
            // táctil pesa en una tabla ordenada por velocidad, no para premiar.
            abrir.Parameters.AddWithValue("@plataforma", plataforma);

            await abrir.ExecuteNonQueryAsync(ct);
            idNueva = abrir.LastInsertedId;
        }

        await tx.CommitAsync(ct);
        return (idNueva, abandonada);
    }

    /// <summary>Busca una corrida abierta de esa cuenta, por id.</summary>
    public async Task<OpenRun?> FindOpenAsync(long idRun, long idCuenta, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        // El id_cuenta va en el WHERE, no se chequea después: sin eso, cualquiera
        // podría cerrar la corrida de otro mandando un id ajeno.
        await using var cmd = new MySqlCommand(
            """
            SELECT id, seed, datos_version FROM runs
            WHERE id = @id AND id_cuenta = @cuenta AND motivo_cierre IS NULL
            LIMIT 1
            """, conn);

        cmd.Parameters.AddWithValue("@id", idRun);
        cmd.Parameters.AddWithValue("@cuenta", idCuenta);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return new OpenRun(r.GetInt64(0), r.GetUInt64(1), r.GetInt32(2));
    }

    /// <summary>
    /// Cierra la corrida con lo que reclama el cliente.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>validado</c> se queda en 0. Lo que entra acá es un <b>reclamo</b>. No
    /// hace falta revalidar desde el día uno, pero sí guardar desde el día uno lo
    /// necesario para poder hacerlo: la seed está en la fila y el log entra acá.
    /// Lo que no se escribió hoy no se agrega mañana.
    /// </remarks>
    public async Task CloseAsync(
        long idRun, bool gano, int oleada, int area, int ticks,
        byte[]? log, bool logTruncado, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            UPDATE runs SET
                gano          = @gano,
                oleada        = @oleada,
                area          = @area,
                ticks         = @ticks,
                motivo_cierre = @motivo,
                log_acciones  = @log,
                log_truncado  = @truncado,
                terminada     = UTC_TIMESTAMP(3)
            WHERE id = @id AND motivo_cierre IS NULL
            """, conn);

        cmd.Parameters.AddWithValue("@id", idRun);
        cmd.Parameters.AddWithValue("@gano", gano ? 1 : 0);
        cmd.Parameters.AddWithValue("@oleada", oleada);
        cmd.Parameters.AddWithValue("@area", area);
        cmd.Parameters.AddWithValue("@ticks", ticks);
        cmd.Parameters.AddWithValue("@motivo", gano ? "gano" : "perdio");
        cmd.Parameters.AddWithValue("@log", (object?)log ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@truncado", logTruncado ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Guarda el progreso de la corrida abierta: log, tick y hasta dónde llegó.
    /// </summary>
    /// <remarks>
    /// <para>Es el mismo trío que escribe <c>SaveFile</c> del lado del cliente
    /// —seed, log de acciones y tick—, con la seed ya en la fila desde
    /// <c>/run/start</c>. No hay estado serializado: cargar es re-simular.</para>
    ///
    /// <para>⚠ <b>El progreso NUNCA retrocede.</b> La condición
    /// <c>@ticks >= ticks</c> va en el <c>WHERE</c>, así que la decide MySQL y no
    /// hay ventana entre leer y escribir. Sin eso, un celular que quedó abierto
    /// en la oleada 12 sube su estado viejo encima de la 60 de la PC y le
    /// rebobina la corrida al jugador. Con la guarda, dos dispositivos a la vez
    /// se pisan —cada uno le gana al otro— pero <b>nunca hacia atrás</b>: lo peor
    /// que pasa es perder las jugadas del que iba más atrasado.</para>
    ///
    /// <para>⚠ <c>validado</c> no se toca, igual que en <see cref="CloseAsync"/>.
    /// Un checkpoint es un reclamo como cualquier otro — con la diferencia de que
    /// este trae el log, así que <b>sí</b> se puede revalidar.</para>
    /// </remarks>
    /// <returns><c>false</c> si no hay corrida abierta con ese id, o si el progreso iba para atrás.</returns>
    public async Task<bool> SaveProgressAsync(
        long idRun, long idCuenta, int oleada, int area, int ticks, int tickGuardado,
        byte[]? log, bool logTruncado, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            UPDATE runs SET
                oleada        = @oleada,
                area          = @area,
                ticks         = @ticks,
                tick_guardado = @tick,
                log_acciones  = @log,
                log_truncado  = @truncado,
                actualizada   = UTC_TIMESTAMP(3)
            WHERE id = @id
              AND id_cuenta = @cuenta
              AND motivo_cierre IS NULL
              AND @ticks >= ticks
            """, conn);

        cmd.Parameters.AddWithValue("@id", idRun);
        cmd.Parameters.AddWithValue("@cuenta", idCuenta);
        cmd.Parameters.AddWithValue("@oleada", oleada);
        cmd.Parameters.AddWithValue("@area", area);
        cmd.Parameters.AddWithValue("@ticks", ticks);
        cmd.Parameters.AddWithValue("@tick", tickGuardado);
        cmd.Parameters.AddWithValue("@log", (object?)log ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@truncado", logTruncado ? 1 : 0);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// La corrida abierta de una cuenta, con todo lo que hace falta para retomarla.
    /// </summary>
    /// <remarks>
    /// <para>Es lo que hace que la corrida de la PC se siga en el celular: la
    /// seed más el log más el tick reconstruyen el estado exacto, porque la
    /// simulación es determinista y va <b>toda en enteros</b> con PRNG propio. Un
    /// solo <c>double</c> adentro del tick y ARM y x86 divergirían; esa regla se
    /// escribió para el anticheat y acá se cobra sola.</para>
    ///
    /// <para>Devuelve <c>datos_version</c> para que el cliente pueda negarse a
    /// reproducir con otros números: un guardado es un log que se re-simula, así
    /// que <b>solo vale contra los mismos datos con los que se jugó</b>. Es la
    /// misma regla que ya rige el ranking por temporadas.</para>
    /// </remarks>
    public async Task<SavedRun?> FindCurrentAsync(long idCuenta, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            SELECT id, seed, datos_version, oleada, area, ticks, tick_guardado,
                   log_acciones, log_truncado, plataforma, actualizada
            FROM runs
            WHERE id_cuenta = @cuenta AND motivo_cierre IS NULL
            LIMIT 1
            """, conn);

        cmd.Parameters.AddWithValue("@cuenta", idCuenta);

        await using var r = await cmd.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct))
            return null;

        return new SavedRun(
            Id: r.GetInt64(0),
            Seed: r.GetUInt64(1),
            DataVersion: r.GetInt32(2),
            Wave: r.GetInt32(3),
            Area: r.GetInt32(4),
            Ticks: r.GetInt32(5),
            SavedTick: r.GetInt32(6),
            Log: r.IsDBNull(7) ? null : (byte[])r.GetValue(7),
            LogTruncated: !r.IsDBNull(8) && r.GetInt32(8) != 0,
            Platform: r.IsDBNull(9) ? "pc" : r.GetString(9),
            UpdatedUtc: r.IsDBNull(10) ? null : r.GetDateTime(10));
    }

    /// <summary>
    /// La tabla de posiciones.
    /// </summary>
    /// <remarks>
    /// <para><b>El orden es <c>gano DESC, oleada DESC, ticks ASC</c>:</b> arriba
    /// los que ganaron y entre ellos el más rápido; abajo los que no, por hasta
    /// dónde llegaron. Son dos enteros que el jugador ve en pantalla, sin fórmula
    /// compuesta que haya que explicar.</para>
    ///
    /// <para>Con el tiempo la punta pasa a ser <b>una tabla de velocidad</b>, y
    /// por eso existe adelantar oleada: sin esa mecánica el desempate por
    /// <c>ticks</c> no mide nada.</para>
    ///
    /// <para><c>datos_version</c> va primero en el <c>WHERE</c> y primero en el
    /// índice: cada rebalanceo es una temporada, y las viejas siguen
    /// consultables.</para>
    ///
    /// <para>⚠ <b>No hay filtro de mapa.</b> Con corrida roguelike continua el
    /// área no se elige: se llega o no se llega.</para>
    /// </remarks>
    public async Task<List<RankingRow>> RankingAsync(int datosVersion, int limite, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            SELECT c.nick, r.gano, r.oleada, r.area, r.ticks, r.validado
            FROM runs r
            JOIN cuentas c ON c.id = r.id_cuenta
            WHERE r.datos_version = @version
              AND r.motivo_cierre IN ('gano', 'perdio')
            ORDER BY r.gano DESC, r.oleada DESC, r.ticks ASC
            LIMIT @limite
            """, conn);

        cmd.Parameters.AddWithValue("@version", datosVersion);
        cmd.Parameters.AddWithValue("@limite", limite);

        var filas = new List<RankingRow>(limite);
        int puesto = 0;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            puesto++;
            filas.Add(new RankingRow(
                puesto,
                r.GetString(0),
                r.GetInt32(1) != 0,
                r.GetInt32(2),
                r.GetInt32(3),
                r.GetInt32(4),
                r.GetInt32(5) != 0));
        }

        return filas;
    }
}
