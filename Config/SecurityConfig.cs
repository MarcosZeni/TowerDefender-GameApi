namespace TowerOfArgentum.Api.Config;

/// <summary>
/// Parámetros de seguridad.
/// </summary>
/// <remarks>
/// ⚠ <b>Los de Argon2id se copian del LoginServer sin cambiar un número.</b> Son
/// <c>m=19456, t=2, p=1</c>, la configuración recomendada por OWASP. No es
/// cabalística: los parámetros se guardan <b>adentro</b> de cada hash, así que
/// cambiarlos no invalida las contraseñas viejas —se re-hashean solas en el
/// próximo login— pero sí dispara un re-hasheo masivo que no hace falta.
/// </remarks>
public sealed class SecurityConfig
{
    /// <summary>
    /// Cuánto vale un token, en segundos.
    /// </summary>
    /// <remarks>
    /// <para>El LoginServer usa 30 segundos, porque su token es un pase de un
    /// solo uso: se emite y se canjea enseguida contra el server de juego.</para>
    ///
    /// <para>Acá el token <b>es la sesión</b>: el jugador cierra el juego y al
    /// reabrirlo tiene que seguir logueado. 30 días.</para>
    ///
    /// <para>⚠ Esto es del token, no de la corrida. Una corrida <b>no caduca por
    /// tiempo</b>.</para>
    /// </remarks>
    public int TokenTtlSeconds { get; init; } = 30 * 24 * 3600;

    /// <summary>
    /// Largo del nick, y el mínimo de la contraseña.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>Estos son de despliegue y no de balance</b>, y por eso viven en
    /// <c>appsettings.json</c> y no en <c>Data\</c>: son política de la cuenta,
    /// no números del juego. Ver <see cref="ServerData"/> para la línea entre
    /// las dos cosas.</para>
    ///
    /// <para>⚠ <b><see cref="MaxNick"/> tiene que entrar en la columna.</b>
    /// <c>cuentas.nick</c> es <c>VARCHAR(20)</c>: subir esto sin migrar la tabla
    /// deja registros que la base recorta o rechaza según el <c>sql_mode</c>.</para>
    ///
    /// <para>El texto de <c>ErrorResponse.BadNick</c> dice «entre 3 y 20» a
    /// mano: si se cambian estos números, hay que cambiarlo también.</para>
    /// </remarks>
    public int MinNick { get; init; } = 3;

    public int MaxNick { get; init; } = 20;

    public int MinPassword { get; init; } = 8;

    /// <summary>
    /// Cuántos días de <c>intentos_login</c> se conservan.
    /// </summary>
    /// <remarks>
    /// El barrido corre amortizado, colgado del login, y no como tarea aparte:
    /// una tabla que solo se llena cuando alguien intenta entrar se puede limpiar
    /// cuando alguien intenta entrar.
    /// </remarks>
    public int LoginAttemptDays { get; init; } = 90;

    /// <summary>
    /// Tope de filas de <c>GET /ranking</c>, pida lo que pida el cliente.
    /// </summary>
    /// <remarks>
    /// El endpoint es público y sin token: sin techo, un <c>?limite=1000000</c>
    /// es una consulta cara gratis para cualquiera.
    /// </remarks>
    public int MaxRankingRows { get; init; } = 500;

    public int MaxAttemptsPerIpPerMinute { get; init; } = 10;

    public int MaxFailuresPerNick { get; init; } = 15;

    public int NickWindowMinutes { get; init; } = 15;

    /// <summary>
    /// Hashes de Argon2id en paralelo. Fija el techo de memoria: slots ×
    /// <see cref="Argon2MemoryKib"/>. Con 10 son 190 MB, entren 10 jugadores o
    /// entren 10.000.
    /// </summary>
    public int MaxConcurrentHashes { get; init; } = 10;

    public int HashQueueTimeoutSeconds { get; init; } = 3;

    public int Argon2MemoryKib { get; init; } = 19456;

    public int Argon2Iterations { get; init; } = 2;

    public int Argon2Parallelism { get; init; } = 1;

    public int MaxConcurrentConnections { get; init; } = 1000;

    // ⚠ Acá había cinco alias en inglés —Argon2MemoryKib, MaxConcurrentHashes y
    // compañía— que existían solo porque las propiedades de verdad estaban en
    // español y `PasswordHasher` y `HashGate`, copiados del LoginServer sin
    // tocar, esperaban los nombres de allá.
    //
    // Con las propiedades ya en inglés los alias son el mismo nombre dos veces,
    // así que se fueron. Los valores y el significado no cambiaron.
}
