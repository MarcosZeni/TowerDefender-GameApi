using MySqlConnector;

namespace TowerOfArgentum.Api.Data;

/// <summary>
/// Verifica al arrancar que la base tiene lo que esta API necesita.
/// </summary>
/// <remarks>
/// <para>Las migraciones se corren <b>a mano</b>: no hay ORM, no hay migraciones
/// automáticas, no hay tabla de control. El modo de fallar es siempre el mismo
/// —deploy nuevo, servicio arriba, base sin migrar— y sin este chequeo se
/// descubre recién en el primer pedido de un jugador, como un 500 con un mensaje
/// de MySQL que no dice qué hacer.</para>
///
/// <para>Copiado en espíritu del <c>SchemaCheck</c> del LoginServer, con las
/// tablas de este proyecto.</para>
/// </remarks>
public sealed class SchemaCheck(Database db)
{
    /// <summary>Qué tabla tiene que existir y con qué columnas críticas.</summary>
    private static readonly (string Tabla, string[] Columns, string Archivo)[] _requerido =
    [
        ("cuentas", ["id", "nick", "password", "oro"], "001_cuentas.sql"),
        ("tokens", ["token_hash", "id_cuenta", "expira"], "002_tokens.sql"),

        // `seed`, `datos_version` y `log_acciones` están en la lista a propósito:
        // son lo que hace falta para poder REVALIDAR una corrida más adelante. Si
        // arrancara sin ellas, las corridas se guardarían igual y quedarían
        // imposibles de verificar para siempre — lo que no se escribió hoy no se
        // agrega mañana.
        ("runs", ["id", "id_cuenta", "seed", "gano", "oleada", "ticks",
                  "motivo_cierre", "datos_version", "log_acciones", "validado"], "003_runs.sql"),

        // Las tres columnas del progreso guardado van en su propia entrada y no
        // sumadas a la de arriba, para que el mensaje de arranque nombre la
        // migración 006 y no la 003: quien tenga la base vieja necesita correr
        // esa, no la que ya corrió.
        ("runs", ["tick_guardado", "actualizada", "plataforma"], "006_runs_progreso.sql"),
        ("runs", ["datos_hash"], "007_runs_datos_hash.sql"),

        ("planos_cuenta", ["id_cuenta", "plano", "precio"], "004_planos_cuenta.sql"),
        ("intentos_login", ["ip", "nick", "exito", "cuando"], "005_intentos_login.sql"),
    ];

    public sealed record Resultado(bool Alcanzable, string? ErrorConexion, List<string> Problems);

    public async Task<Resultado> RunAsync(CancellationToken ct)
    {
        var problemas = new List<string>();

        try
        {
            await using var conn = await db.OpenAsync(ct);

            foreach (var (tabla, columnas, archivo) in _requerido)
            {
                var presentes = await ColumnasDeAsync(conn, tabla, ct);

                if (presentes.Count == 0)
                {
                    problemas.Add($"falta la tabla `{tabla}` — aplicá sql/{archivo}");
                    continue;
                }

                foreach (string columna in columnas)
                {
                    if (!presentes.Contains(columna))
                        problemas.Add($"a `{tabla}` le falta la columna `{columna}` — revisá sql/{archivo}");
                }
            }
        }
        catch (MySqlException e)
        {
            // No se aborta el arranque: MySQL puede tardar en levantar y de eso el
            // servicio se recupera solo. /health lo va a decir mientras tanto.
            return new Resultado(false, e.Message, problemas);
        }

        return new Resultado(true, null, problemas);
    }

    private static async Task<HashSet<string>> ColumnasDeAsync(
        MySqlConnection conn, string tabla, CancellationToken ct)
    {
        // DATABASE() y no un nombre fijo: así el chequeo sirve igual si alguien
        // apunta la API a una base de prueba con otro nombre.
        await using var cmd = new MySqlCommand(
            """
            SELECT COLUMN_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tabla
            """, conn);

        cmd.Parameters.AddWithValue("@tabla", tabla);

        var columnas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            columnas.Add(r.GetString(0));

        return columnas;
    }
}
