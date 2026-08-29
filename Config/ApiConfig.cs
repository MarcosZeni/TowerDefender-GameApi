namespace TowerOfArgentum.Api.Config;

/// <summary>
/// La configuración del servidor, mapeada desde <c>appsettings.json</c>.
///
/// Arranca chica a propósito: hoy solo hace falta lo que necesita
/// <c>/health</c>. Los parámetros de Argon2id, el TTL del token y los frenos
/// por IP entran cuando entre la Fase 4 (PASOS.md), copiados del
/// <c>LoginServer</c> sin cambiar un número.
/// </summary>
public sealed class ApiConfig
{
    /// <summary>
    /// Cadena de conexión de respaldo. En un hosting conviene pasarla por la
    /// variable de entorno <c>ConnectionStrings__Default</c>, así la contraseña
    /// de MySQL no queda escrita en un archivo en disco.
    /// </summary>
    public string ConnectionString { get; init; } = "";

    /// <summary>
    /// Versión de los datos de balance con la que corre este servidor.
    ///
    /// No es cosmético: los JSON de <c>Client\Data\</c> son entrada de la
    /// simulación, así que cambiar un número de balance vuelve irreplicables
    /// las corridas viejas. Cada rebalanceo es una temporada del ranking, y
    /// este número es el que las separa (DATOS.md §7).
    /// </summary>
    public int DataVersion { get; init; } = 1;

    /// <summary>
    /// Hash de los datos de balance con los que corre el servidor.
    /// </summary>
    /// <remarks>
    /// Se compara contra el que manda el cliente en <c>/run/start</c>. Si no
    /// coinciden, el cliente está jugando con otros números y la corrida no sería
    /// reproducible ni comparable con las demás. <b>Rechazarla al empezar es
    /// infinitamente mejor que rechazarla después de tres horas.</b>
    ///
    /// <para>Vacío = no se verifica, para desarrollo.</para>
    /// </remarks>
    public string DataHash { get; init; } = "";

    public SecurityConfig Security { get; init; } = new();
}
