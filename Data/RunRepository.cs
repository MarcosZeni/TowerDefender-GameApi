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
        long idCuenta, ulong seed, int datosVersion, CancellationToken ct)
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
            INSERT INTO runs (id_cuenta, seed, datos_version, iniciada)
            VALUES (@cuenta, @seed, @version, UTC_TIMESTAMP(3))
            """, conn, tx))
        {
            abrir.Parameters.AddWithValue("@cuenta", idCuenta);
            abrir.Parameters.AddWithValue("@seed", seed);
            abrir.Parameters.AddWithValue("@version", datosVersion);

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
