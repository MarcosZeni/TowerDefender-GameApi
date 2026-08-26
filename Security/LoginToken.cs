using System.Buffers.Text;
using System.Security.Cryptography;

namespace TowerOfArgentum.Api.Security;

/// <summary>
/// El token que la API le da al cliente para que lo canjee en el server de juego.
///
/// <para><b>32 bytes de CSPRNG.</b> El del sistema viejo salía de
/// <c>Math.random()</c> —el PRNG de V8, no criptográfico— así que observando
/// tokens se podían predecir los siguientes. Tampoco sirve
/// <c>Guid.NewGuid()</c>: es único, no impredecible.</para>
///
/// <para><b>Se guarda hasheado</b> con SHA-256. Acá el hash rápido está bien, y
/// no es una contradicción con usar Argon2id para las contraseñas: el costo del
/// hash existe para compensar la poca entropía de lo que elige una persona.
/// Contra 256 bits de aleatoriedad real no hay diccionario ni fuerza bruta que
/// sirva, por veloz que sea el algoritmo. Guardarlo hasheado significa que quien
/// lea la tabla no puede canjear nada.</para>
/// </summary>
public static class LoginToken
{
    public const int Bytes = 32;

    /// <summary>Genera un token nuevo: lo que viaja al cliente y lo que se guarda.</summary>
    public static (string Plain, byte[] Hash) Create()
    {
        byte[] raw = RandomNumberGenerator.GetBytes(Bytes);
        return (Base64Url.EncodeToString(raw), SHA256.HashData(raw));
    }

    /// <summary>Hash del token que llegó, para buscarlo en la tabla. Devuelve
    /// <c>null</c> si el texto no es un token con forma válida — así una cadena
    /// cualquiera se descarta antes de tocar la base.</summary>
    public static byte[]? HashOf(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;

        Span<byte> raw = stackalloc byte[Bytes];
        return Base64Url.TryDecodeFromChars(plain, raw, out int written) && written == Bytes
            ? SHA256.HashData(raw)
            : null;
    }
}
