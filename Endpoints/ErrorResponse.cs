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
public sealed record ErrorResponse(string Codigo, string Mensaje)
{
    public static readonly ErrorResponse CredencialesInvalidas =
        new("credenciales_invalidas", "El nombre o la contraseña no son correctos.");

    public static readonly ErrorResponse NickEnUso =
        new("nick_en_uso", "Ese nombre ya está tomado.");

    public static readonly ErrorResponse NickInvalido =
        new("nick_invalido", "El nombre tiene que tener entre 3 y 20 caracteres, sin espacios.");

    public static readonly ErrorResponse PasswordInvalida =
        new("password_invalida", "La contraseña tiene que tener al menos 8 caracteres.");

    public static readonly ErrorResponse NoAutorizado =
        new("no_autorizado", "Falta el token o ya no vale. Volvé a entrar.");

    public static readonly ErrorResponse DemasiadosIntentos =
        new("demasiados_intentos", "Demasiados intentos. Esperá un minuto.");

    public static readonly ErrorResponse Sobrecargado =
        new("sobrecargado", "El servidor está ocupado. Probá de nuevo en unos segundos.");

    public static readonly ErrorResponse RunNoEncontrada =
        new("run_no_encontrada", "Esa corrida no existe o no es tuya.");

    public static readonly ErrorResponse RunYaCerrada =
        new("run_ya_cerrada", "Esa corrida ya está cerrada.");

    public static readonly ErrorResponse DatosDesactualizados =
        new("datos_desactualizados", "Tu versión de los datos de balance no coincide con la del servidor.");

    public static readonly ErrorResponse PlanoDesconocido =
        new("plano_desconocido", "Ese plano no existe.");

    public static readonly ErrorResponse PlanoYaAprendido =
        new("plano_ya_aprendido", "Ya tenés ese plano.");

    public static readonly ErrorResponse OroInsuficiente =
        new("oro_insuficiente", "No te alcanza el oro.");

    public static readonly ErrorResponse ReclamoInconsistente =
        new("reclamo_inconsistente", "Los datos de la corrida no son coherentes.");
}
