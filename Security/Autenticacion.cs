using TowerOfArgentum.Api.Data;

namespace TowerOfArgentum.Api.Security;

/// <summary>
/// Valida el token de cada request.
/// </summary>
/// <remarks>
/// <para>⚠ <b>Escrito de cero, no copiado.</b> El <c>LoginServer</c> de Austral
/// emite tokens y nunca los canjea: eso lo hace el server de juego, que es otro
/// proceso y habla el protocolo binario. Acá el canje pasa en la misma API, así
/// que este archivo no tiene equivalente allá.</para>
///
/// <para>La cadena es: <c>Authorization: Bearer &lt;token&gt;</c> →
/// <see cref="LoginToken.HashOf"/> → lookup por PK → chequeo de
/// <c>expira</c>.</para>
/// </remarks>
public static class Autenticacion
{
    private const string Encabezado = "Authorization";
    private const string Prefijo = "Bearer ";

    /// <summary>
    /// Saca el token del header. <c>null</c> si no está o no tiene forma válida.
    /// </summary>
    public static string? TokenDe(HttpContext http)
    {
        string? crudo = http.Request.Headers[Encabezado];

        if (string.IsNullOrEmpty(crudo))
            return null;

        // StringComparison.Ordinal explícito: el default de string.StartsWith es
        // sensible a la cultura, y con InvariantGlobalization prendido eso ya es
        // raro, pero además acá comparar por cultura no significa nada — es un
        // token, no texto humano.
        if (!crudo.StartsWith(Prefijo, StringComparison.Ordinal))
            return null;

        return crudo[Prefijo.Length..];
    }

    /// <summary>
    /// Devuelve la cuenta dueña del token, o <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="LoginToken.HashOf"/> devuelve <c>null</c> si el texto no tiene
    /// forma de token, así que una cadena cualquiera se descarta <b>antes</b> de
    /// tocar la base. Sin eso, cualquiera puede hacer que la API consulte MySQL
    /// mandando basura en un header.
    /// </remarks>
    public static async Task<long?> CuentaDeAsync(HttpContext http, TokenRepositorio tokens, CancellationToken ct)
    {
        string? plano = TokenDe(http);
        if (plano is null)
            return null;

        byte[]? hash = LoginToken.HashOf(plano);
        if (hash is null)
            return null;

        return await tokens.CanjearAsync(hash, ct);
    }
}
