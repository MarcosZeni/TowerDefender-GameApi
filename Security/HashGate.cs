using TowerOfArgentum.Api.Config;

namespace TowerOfArgentum.Api.Security;

/// <summary>
/// Limita cuántas verificaciones de contraseña corren a la vez.
///
/// <para><b>Por qué existe.</b> Argon2id reserva memoria mientras calcula —19 MiB
/// con la config por defecto— y la libera al terminar, unos 40 ms después. O sea
/// que el pico de memoria del servicio <b>no lo fija la cantidad de jugadores,
/// lo fija cuántos hashes se dejen correr en paralelo</b>. Sin este tope, una
/// avalancha de 10.000 logins simultáneos pediría 190 GB y se lleva puesto el
/// hosting. Con un tope de 10, el techo son 190 MB constantes, entren 10 o
/// entren 10.000.</para>
///
/// <para><b>El tope no se elige por presupuesto de RAM, se elige por núcleos.</b>
/// El hash es CPU pura, así que la capacidad real es núcleos ÷ tiempo-de-hash.
/// Subir los slots por encima de ~2 por núcleo no atiende ni un login más por
/// segundo: solo reserva más memoria y alarga cada hash.</para>
///
/// <para>La reserva sucede <b>adentro</b> del turno: sin lugar en el semáforo el
/// delegate no se ejecuta y no se aloca un solo byte. Por eso el techo es
/// aritmético —slots × memoria— y no algo que se mide y se corrige tarde. El
/// atacante no controla ninguno de los dos factores.</para>
///
/// <para><b>No es el rate limiter.</b> Son dos controles distintos y hacen falta
/// los dos: el rate limiter es por IP y <i>rechaza</i> al que abusa; esto es
/// global y <i>encola</i> al jugador legítimo. Este va después del rate limiter
/// y antes de reservar un solo byte.</para>
///
/// <para>Si la espera pasa del tiempo configurado se contesta 503 en vez de
/// apilar cola: fallar rápido antes que morir lento.</para>
/// </summary>
public sealed class HashGate : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _espera;

    public int MaxConcurrent { get; }

    public HashGate(SecurityConfig config)
    {
        MaxConcurrent = config.MaxConcurrentHashes > 0
            ? config.MaxConcurrentHashes
            : Environment.ProcessorCount;

        _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        _espera = TimeSpan.FromSeconds(Math.Max(1, config.HashQueueTimeoutSeconds));
    }

    /// <summary>Techo de memoria en MB, para poder loguearlo al arrancar y que
    /// el número esté a la vista en vez de ser una sorpresa en producción.</summary>
    public int MemoryCeilingMb(SecurityConfig config)
        => (int)((long)MaxConcurrent * config.Argon2MemoryKib / 1024);

    /// <summary>
    /// Corre el trabajo con un lugar tomado. <c>Ok = false</c> significa que no
    /// consiguió turno a tiempo: el que llama contesta 503 con Retry-After.
    /// </summary>
    public async Task<(bool Ok, T Value)> TryRunAsync<T>(Func<T> work, CancellationToken ct)
    {
        if (!await _slots.WaitAsync(_espera, ct).ConfigureAwait(false))
            return (false, default!);

        try
        {
            // El hash es CPU puro y bloqueante: va al thread pool para no clavar
            // el hilo que atiende el request.
            return (true, await Task.Run(work, ct).ConfigureAwait(false));
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}
