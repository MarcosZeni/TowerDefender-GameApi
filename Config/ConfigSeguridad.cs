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
public sealed class ConfigSeguridad
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
    public int TokenTtlSegundos { get; init; } = 30 * 24 * 3600;

    public int MaxIntentosPorIpPorMinuto { get; init; } = 10;

    public int MaxFallosPorNick { get; init; } = 15;

    public int VentanaNickMinutos { get; init; } = 15;

    /// <summary>
    /// Hashes de Argon2id en paralelo. Fija el techo de memoria: slots ×
    /// <see cref="Argon2MemoriaKib"/>. Con 10 son 190 MB, entren 10 jugadores o
    /// entren 10.000.
    /// </summary>
    public int MaxHashesConcurrentes { get; init; } = 10;

    public int TimeoutColaHashSegundos { get; init; } = 3;

    public int Argon2MemoriaKib { get; init; } = 19456;

    public int Argon2Iteraciones { get; init; } = 2;

    public int Argon2Paralelismo { get; init; } = 1;

    public int MaxConexionesConcurrentes { get; init; } = 1000;

    // Los nombres que esperan PasswordHasher y HashGate, copiados del
    // LoginServer sin tocar. Se exponen como alias para no tener que editar esos
    // dos archivos: cuanto menos se los toque, más fácil es verificar que siguen
    // siendo los mismos.
    public int Argon2MemoryKib => Argon2MemoriaKib;
    public int Argon2Iterations => Argon2Iteraciones;
    public int Argon2Parallelism => Argon2Paralelismo;
    public int MaxConcurrentHashes => MaxHashesConcurrentes;
    public int HashQueueTimeoutSeconds => TimeoutColaHashSegundos;
}
