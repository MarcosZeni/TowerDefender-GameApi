using System.Text.Json.Serialization;

namespace TowerOfArgentum.Api.Endpoints;

/// <summary>
/// Los errores que devuelve la API.
/// </summary>
/// <remarks>
/// <para>Constantes estáticas y no cadenas sueltas, copiado del estilo del
/// LoginServer: un error escrito a mano en cada endpoint termina siendo cinco
/// mensajes distintos para la misma causa, y el cliente no puede distinguirlos
/// programáticamente.</para>
///
/// <para>⚠ <b>En español, con acentos.</b> Es la regla de oro 4 del proyecto y
/// también aplica a los textos de error de la API.</para>
///
/// <para>⚠ Los mensajes de login <b>no distinguen</b> entre "ese nick no existe"
/// y "la contraseña está mal". Decirlo delata qué cuentas existen, y eso es la
/// mitad del trabajo de quien quiere entrar a una.</para>
/// </remarks>
public sealed record ErrorResponse(
    [property: JsonPropertyName("codigo")] string Code,
    [property: JsonPropertyName("mensaje")] string Message)
{
    public static readonly ErrorResponse BadCredentials =
        new("credenciales_invalidas", "El nombre o la contraseña no son correctos.");

    public static readonly ErrorResponse NickTaken =
        new("nick_en_uso", "Ese nombre ya está tomado.");

    public static readonly ErrorResponse BadNick =
        new("nick_invalido", "El nombre tiene que tener entre 3 y 20 caracteres, sin espacios.");

    public static readonly ErrorResponse BadPassword =
        new("password_invalida", "La contraseña tiene que tener al menos 8 caracteres.");

    public static readonly ErrorResponse Unauthorized =
        new("no_autorizado", "Falta el token o ya no vale. Volvé a entrar.");

    public static readonly ErrorResponse TooManyAttempts =
        new("demasiados_intentos", "Demasiados intentos. Esperá un minuto.");

    public static readonly ErrorResponse Busy =
        new("sobrecargado", "El servidor está ocupado. Probá de nuevo en unos segundos.");

    public static readonly ErrorResponse RunNotFound =
        new("run_no_encontrada", "Esa corrida no existe o no es tuya.");

    public static readonly ErrorResponse RunAlreadyClosed =
        new("run_ya_cerrada", "Esa corrida ya está cerrada.");

    public static readonly ErrorResponse StaleData =
        new("datos_desactualizados", "Tu versión de los datos de balance no coincide con la del servidor.");

    public static readonly ErrorResponse UnknownBlueprint =
        new("plano_desconocido", "Ese plano no existe.");

    public static readonly ErrorResponse BlueprintAlreadyLearned =
        new("plano_ya_aprendido", "Ya tenés ese plano.");

    public static readonly ErrorResponse NotEnoughGold =
        new("oro_insuficiente", "No te alcanza el oro.");

    public static readonly ErrorResponse InconsistentClaim =
        new("reclamo_inconsistente", "Los datos de la corrida no son coherentes.");
}
