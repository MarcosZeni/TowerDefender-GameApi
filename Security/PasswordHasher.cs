using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TowerOfArgentum.Api.Config;
using Konscious.Security.Cryptography;

namespace TowerOfArgentum.Api.Security;

/// <summary>
/// Argon2id. Es el único lugar del proyecto donde se hashea una contraseña.
///
/// <para><b>El cliente no hashea nada.</b> Manda la contraseña en claro sobre
/// TLS y acá se deriva el hash. Si hasheara el cliente, ese hash <i>sería</i> la
/// contraseña —quien copiara la base lo reenviaría y entraría—, que es
/// exactamente el agujero del sistema viejo. Además la sal es por usuario y vive
/// en la base: para usarla, el cliente tendría que pedirla antes de loguear, y
/// eso delata qué usuarios existen.</para>
///
/// <para><b>Por qué un hash lento y no SHA-256:</b> SHA-256 no es débil, es
/// RÁPIDO, y para contraseñas eso es el problema. Si alguien se lleva la base
/// prueba candidatas offline al ritmo que le dé su hardware, y una placa de
/// video hace miles de millones de SHA-256 por segundo. Argon2id está diseñado
/// para ser lento Y para consumir memoria, que es lo que le saca la ventaja a
/// las GPU: tienen miles de núcleos pero poca memoria para cada uno.</para>
///
/// <para><b>Formato PHC.</b> El hash se guarda como
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;sal&gt;$&lt;hash&gt;</c>, con los
/// parámetros adentro. Así, subir el costo más adelante no invalida nada: cada
/// contraseña se verifica con los parámetros con los que se creó y se re-hashea
/// sola la próxima vez que su dueño entra.</para>
/// </summary>
public sealed class PasswordHasher(ConfigSeguridad config)
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Argon2Version = 19;

    /// <summary>
    /// Hash de una contraseña con los parámetros actuales de configuración.
    /// Devuelve la cadena PHC completa, lista para guardar.
    /// </summary>
    public string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(password, salt,
            config.Argon2MemoryKib, config.Argon2Iterations, config.Argon2Parallelism);

        return string.Concat(
            "$argon2id$v=", Argon2Version.ToString(CultureInfo.InvariantCulture),
            "$m=", config.Argon2MemoryKib.ToString(CultureInfo.InvariantCulture),
            ",t=", config.Argon2Iterations.ToString(CultureInfo.InvariantCulture),
            ",p=", config.Argon2Parallelism.ToString(CultureInfo.InvariantCulture),
            "$", ToB64(salt), "$", ToB64(hash));
    }

    /// <summary>
    /// Verifica contra una cadena PHC. Devuelve además si conviene re-hashear
    /// porque el hash guardado usa parámetros más flojos que los de hoy.
    /// </summary>
    public (bool Ok, bool NeedsRehash) Verify(string password, string phc)
    {
        if (!TryParse(phc, out var p)) return (false, false);

        byte[] esperado = Derive(password, p.Salt, p.MemoryKib, p.Iterations, p.Parallelism);
        bool ok = CryptographicOperations.FixedTimeEquals(esperado, p.Hash);

        bool viejo = p.MemoryKib < config.Argon2MemoryKib
                     || p.Iterations < config.Argon2Iterations
                     || p.Parallelism != config.Argon2Parallelism;

        return (ok, ok && viejo);
    }

    /// <summary>
    /// Hash de descarte, con el mismo costo que uno real.
    ///
    /// <para>Cuando el usuario no existe hay que gastar lo mismo igual. Si
    /// "usuario inexistente" contestara al instante y "contraseña incorrecta"
    /// tardara 40 ms, cualquiera podría averiguar qué cuentas existen midiendo el
    /// tiempo de respuesta.</para>
    /// </summary>
    public void BurnTime(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        _ = Derive(password, salt,
            config.Argon2MemoryKib, config.Argon2Iterations, config.Argon2Parallelism);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(HashBytes);
    }

    private readonly record struct Parsed(
        byte[] Salt, byte[] Hash, int MemoryKib, int Iterations, int Parallelism);

    private static bool TryParse(string phc, out Parsed parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(phc)) return false;

        // $argon2id$v=19$m=19456,t=2,p=1$<sal>$<hash>
        string[] parts = phc.Split('$');
        if (parts.Length != 6) return false;
        if (!string.Equals(parts[1], "argon2id", StringComparison.Ordinal)) return false;
        if (!string.Equals(parts[2], "v=19", StringComparison.Ordinal)) return false;

        int m = 0, t = 0, p = 0;
        foreach (string campo in parts[3].Split(','))
        {
            if (campo.Length < 3 || campo[1] != '=') return false;
            if (!int.TryParse(campo.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int valor))
                return false;
            switch (campo[0])
            {
                case 'm': m = valor; break;
                case 't': t = valor; break;
                case 'p': p = valor; break;
                default: return false;
            }
        }
        if (m <= 0 || t <= 0 || p <= 0) return false;

        if (!TryFromB64(parts[4], out byte[] salt)) return false;
        if (!TryFromB64(parts[5], out byte[] hash)) return false;

        parsed = new Parsed(salt, hash, m, t, p);
        return true;
    }

    // El formato PHC usa base64 SIN el relleno de '='.
    private static string ToB64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    private static bool TryFromB64(string text, out byte[] data)
    {
        string padded = (text.Length % 4) switch
        {
            2 => text + "==",
            3 => text + "=",
            0 => text,
            _ => "",
        };
        if (padded.Length == 0) { data = []; return false; }

        try
        {
            data = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            data = [];
            return false;
        }
    }
}
