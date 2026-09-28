using MySqlConnector;
using TowerOfArgentum.Api.Config;
using TowerOfArgentum.Simulation;

namespace TowerOfArgentum.Api.Data;

/// <summary>Qué dio revalidar una corrida.</summary>
public sealed record RevalidationResult(
    long IdRun,
    bool Coincide,
    string Detalle,
    int TicksReclamados,
    int TicksReales,
    int OleadaReclamada,
    int OleadaReal);

/// <summary>
/// Toma una corrida de la base, la re-simula y compara.
/// </summary>
/// <remarks>
/// <para><b>Acá se cobra todo el diseño.</b> La simulación es determinista, la
/// seed la puso el servidor y el log guarda decisiones y no resultados; con esas
/// tres cosas, el servidor puede reproducir la corrida entera y ver si el número
/// que reclamó el cliente es el que sale.</para>
///
/// <para>Lo que el cliente manda es un <b>reclamo</b>: entra con
/// <c>validado = 0</c> y se queda así hasta que esto lo confirme. No hace falta
/// correrlo en el camino del <c>finish</c> —re-simular tres horas de juego lleva
/// segundos, pero no hay por qué hacerlo esperando al jugador—: puede correr
/// después, en lote.</para>
///
/// <para>⚠ <b>Un resultado distinto NO prueba trampa por sí solo.</b> También lo
/// producen un cambio de balance sin subir <c>datos_version</c>, un cambio en el
/// orden de las fases del tick, o un bug de la propia simulación. Por eso esto
/// devuelve un detalle y no un veredicto: el que decide qué hacer con una corrida
/// que no coincide es una persona.</para>
/// </remarks>
public sealed class Revalidator(Database db, ServerData datos, SimulationDataCatalog catalog)
{
    /// <summary>
    /// Revalida una corrida y actualiza su columna <c>validado</c>.
    /// </summary>
    public async Task<RevalidationResult?> RevalidateAsync(
        long idRun, CancellationToken ct)
    {
        var fila = await LeerAsync(idRun, ct);

        if (fila is null)
            return null;

        var (seed, ganoReclamado, oleadaReclamada, ticksReclamados, blob, version, hash) = fila.Value;

        // Sin snapshot verificable no se acusa al jugador ni se usa el balance actual.
        var balance = hash is null ? null : catalog.Find(hash, version);
        if (balance is null)
            return new RevalidationResult(idRun, false,
                $"no está disponible el snapshot de datos v{version} ({hash ?? "sin hash"})",
                ticksReclamados, 0, oleadaReclamada, 0);


        if (blob is null || blob.Length == 0)
        {
            // ⚠ Se marca ILEGIBLE y no se deja en 0. Una fila que vuelve sin
            // marcar la toma otra vez el lote siguiente, y el siguiente: un log
            // que no se lee hoy no se va a leer mañana, así que sin esto
            // `RevalidatePendingAsync` re-simula las mismas filas para siempre.
            await MarcarIlegibleAsync(idRun, ct);

            return new RevalidationResult(
                idRun, false, "sin log: no se puede revalidar",
                ticksReclamados, 0, oleadaReclamada, 0);
        }

        ActionLog log;

        try
        {
            log = BinaryLog.Read(blob);
        }
        catch (Exception ex)
        {
            // El blob puede venir cortado por `maxLogBytes`, que parte el stream
            // Deflate al medio: `Read` tira, no devuelve un log corto.
            await MarcarIlegibleAsync(idRun, ct);

            return new RevalidationResult(
                idRun, false, $"el log no se pudo leer ({ex.Message})",
                ticksReclamados, 0, oleadaReclamada, 0);
        }

        if (log.Count == 0)
        {
            await MarcarIlegibleAsync(idRun, ct);

            return new RevalidationResult(
                idRun, false, "el log no se pudo leer o está vacío",
                ticksReclamados, 0, oleadaReclamada, 0);
        }

        // ⚠ El layout sale de la MISMA cuenta que en el cliente. Si las dos
        // fórmulas se separaran, el servidor re-simularía en otro mapa y todas
        // las corridas darían distinto.
        var mapa = Layouts.Choose(seed, balance);

        var partida = new Run(seed, mapa, log, balance);
        partida.Start();

        // ⚠ Los mismos DOS cortes que `SaveFile.Rebuild` del cliente, y por el
        // mismo motivo. `MaxRevalidationTicks` acota el reloj, pero el reloj es
        // justo lo que se congela en una pausa: un log que termina en la pantalla
        // de preparación —o que diverge y deja acciones sin consumir— deja este
        // `while` girando para siempre, y el que se cuelga acá es el proceso del
        // servidor. Un cuelgue mudo es mucho más caro de diagnosticar que una
        // corrida marcada como no coincidente.
        string? trabado = null;

        while (partida.State == RunState.InProgress &&
               partida.Tick < datos.Limits.MaxRevalidationTicks)
        {
            if (partida.WaitsForPlayer)
            {
                trabado = $"el log se quedó esperando al jugador en el tick {partida.Tick} " +
                          $"(acción {partida.LogCursor} de {log.Count})";
                break;
            }

            int tickPrevio = partida.Tick;
            int cursorPrevio = partida.LogCursor;

            partida.Advance();

            // Una vuelta que no movió ni el reloj ni el cursor no va a mover nada.
            if (partida.Tick == tickPrevio && partida.LogCursor == cursorPrevio)
            {
                trabado = $"la re-simulación se trabó en el tick {partida.Tick} " +
                          $"(acción {partida.LogCursor} de {log.Count}, en pausa {partida.Paused})";
                break;
            }
        }

        bool ganoReal = partida.State == RunState.Won;

        bool coincide =
            trabado is null &&
            partida.Tick == ticksReclamados &&
            partida.Wave == oleadaReclamada &&
            ganoReal == ganoReclamado;

        string detalle = coincide
            ? "coincide"
            : trabado ??
              $"reclamó oleada {oleadaReclamada} en {ticksReclamados} ticks " +
              $"(gano={ganoReclamado}); la re-simulación da oleada {partida.Wave} " +
              $"en {partida.Tick} ticks (gano={ganoReal})";

        await MarcarAsync(idRun, coincide, ct);

        return new RevalidationResult(
            idRun, coincide, detalle,
            ticksReclamados, partida.Tick, oleadaReclamada, partida.Wave);
    }

    /// <summary>
    /// Revalida las corridas cerradas que todavía no se revisaron.
    /// </summary>
    /// <remarks>
    /// Pensado para correr en lote, a mano o desde una tarea programada. De a
    /// pocas: cada una es una simulación completa y puede llevar segundos de CPU.
    /// </remarks>
    public async Task<List<RevalidationResult>> RevalidatePendingAsync(
        int cuantas, CancellationToken ct)
    {
        var ids = new List<long>();

        await using (var conn = await db.OpenAsync(ct))
        await using (var cmd = new MySqlCommand(
            """
            SELECT id FROM runs
            WHERE validado = 0
              AND motivo_cierre IN ('gano', 'perdio')
              AND log_acciones IS NOT NULL
              AND log_truncado = 0
            ORDER BY id
            LIMIT @cuantas
            """, conn))
        {
            cmd.Parameters.AddWithValue("@cuantas", cuantas);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                ids.Add(r.GetInt64(0));
        }

        var salida = new List<RevalidationResult>(ids.Count);

        foreach (long id in ids)
        {
            var resultado = await RevalidateAsync(id, ct);

            if (resultado is not null)
                salida.Add(resultado);
        }

        return salida;
    }

    private async Task<(ulong Seed, bool Won, int Wave, int Ticks, byte[]? Log, int Version, string? Hash)?> LeerAsync(
        long idRun, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            """
            SELECT seed, gano, oleada, ticks, log_acciones, datos_version, datos_hash
            FROM runs WHERE id = @id LIMIT 1
            """, conn);

        cmd.Parameters.AddWithValue("@id", idRun);

        await using var r = await cmd.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct))
            return null;

        byte[]? log = r.IsDBNull(4) ? null : (byte[])r.GetValue(4);

        return (r.GetUInt64(0), r.GetInt32(1) != 0, r.GetInt32(2), r.GetInt32(3), log, r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6));
    }

    /// <summary>
    /// Marca el resultado. <c>1</c> = coincide, <c>2</c> = no coincide.
    /// </summary>
    /// <remarks>
    /// Dos valores distintos de "revisado" y no un booleano: si no coincidir
    /// dejara la columna en 0, cada pasada del lote volvería a re-simular las
    /// mismas corridas fallidas para siempre.
    /// </remarks>
    private async Task MarcarAsync(long idRun, bool coincide, CancellationToken ct)
        => await EscribirValidadoAsync(idRun, coincide ? 1 : 2, ct);

    /// <summary>
    /// Marca la corrida como ILEGIBLE: el log no se puede leer, ni siquiera para
    /// decir si coincide.
    /// </summary>
    /// <remarks>
    /// ⚠ Es un valor propio (3) y no un 2. Un 2 dice «re-simulé y no da», que es
    /// una acusación; esto dice «no hay nada que re-simular», que es un problema
    /// de datos y no del jugador. Y sobre todo saca la fila de la cola: dejarla
    /// en 0 la devuelve al lote siguiente, para siempre.
    /// </remarks>
    private async Task MarcarIlegibleAsync(long idRun, CancellationToken ct)
        => await EscribirValidadoAsync(idRun, 3, ct);

    private async Task EscribirValidadoAsync(long idRun, int validado, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            "UPDATE runs SET validado = @v WHERE id = @id", conn);

        cmd.Parameters.AddWithValue("@id", idRun);
        cmd.Parameters.AddWithValue("@v", validado);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
