using System.Text.Json;
using System.Text.Json.Serialization;

namespace TowerOfArgentum.Api.Config;

/// <summary>
/// Los números que decide el servidor, leídos de <c>GameData\*.json</c>.
/// </summary>
/// <remarks>
/// <para>⚠ <b>Esto no es <see cref="ApiConfig"/>.</b> Son dos cosas distintas y
/// conviene no mezclarlas: <c>ApiConfig</c> es <i>despliegue</i> —a qué base me
/// conecto, cuánto dura un token, cuántos hashes en paralelo— y sale de
/// <c>appsettings.json</c>, que se pisa por entorno y por flags porque cambia de
/// una máquina a otra. Esto es <i>balance</i>: cuánto oro deja una corrida, qué
/// sale un plano. Es el mismo número en todas las máquinas, y quien lo edita
/// quiere leer al lado <b>por qué</b> es ese número.</para>
///
/// <para>Por eso vive en archivos propios con comentarios adentro, como los del
/// cliente, y no en <c>appsettings.json</c>: ahí un comentario no tiene dónde ir
/// y la variable de entorno de un hosting podría cambiar el balance sin que
/// quede rastro.</para>
///
/// <para>⚠ <b>Nada de esto entra a ningún hash.</b> No son entrada de la
/// simulación —el oro de la billetera se calcula cuando la corrida ya terminó, y
/// el precio de un plano es de la cuenta, no de la corrida—, así que tocar un
/// número de acá <b>no es temporada nueva</b>. Lo que sí entra al hash son los
/// JSON de <c>Client\Data\</c>, y por eso el precio de un plano <b>no puede</b>
/// vivir ahí.</para>
///
/// <para>⚠ <b>Las claves del JSON siguen en español y los identificadores no.</b>
/// La costura son los <c>[JsonPropertyName]</c>, el mismo recurso que ya usa el
/// cliente contra los campos de la API: el archivo es un dato y el código es
/// código.</para>
/// </remarks>
public sealed class ServerData
{
    /// <summary>El oro que va a la billetera al terminar una corrida.</summary>
    public required Wallet Wallet { get; init; }

    /// <summary>Con qué se recorta el reclamo del cliente.</summary>
    public required Limits Limits { get; init; }

    /// <summary>
    /// Lo que se vende, por key. <b>Lo que no está acá no se vende.</b>
    /// </summary>
    /// <remarks>
    /// La key es la de <c>Client\Data\planos.json</c> y la que guarda la columna
    /// <c>planos_cuenta.plano</c>. <b>No es el nombre</b>: el nombre vive en los
    /// archivos de idioma del cliente y cambia con el idioma.
    /// </remarks>
    public required IReadOnlyList<BlueprintOnSale> Blueprints { get; init; }

    private Dictionary<string, int>? _porKey;

    /// <summary>El precio de un plano, o <c>null</c> si no se vende.</summary>
    public int? BlueprintPrice(string? plano)
    {
        if (plano is null)
            return null;

        _porKey ??= Blueprints.ToDictionary(p => p.Key, p => p.Price, StringComparer.Ordinal);

        return _porKey.TryGetValue(plano, out int precio) ? precio : null;
    }

    /// <summary>
    /// El oro de la billetera por una corrida.
    /// </summary>
    /// <remarks>
    /// <para><b>Cuadrático en la oleada</b>, no lineal: llegar a la 80 tiene que
    /// valer bastante más que el doble de llegar a la 40, o no hay incentivo para
    /// mejorar una vez que se llegó a un lugar cómodo. El bonus por ganar es
    /// grande y aparte: es el objetivo del juego.</para>
    ///
    /// <para>⚠ Esta plata compra planos, que son progresión permanente. Es el
    /// número más goloso de todo el sistema para un tramposo, y por eso no toca
    /// nunca nada que venga del cliente salvo <paramref name="wave"/>, que el
    /// endpoint ya acotó contra <see cref="Limits.MaxWave"/>.</para>
    /// </remarks>
    public long RunGold(bool won, int wave)
    {
        long baseOro = (long)wave * wave / Wallet.WaveDivisor;

        return won ? baseOro + Wallet.WinBonus : baseOro;
    }
}

/// <summary><c>oro = oleada² / WaveDivisor</c>, más el bonus si ganó.</summary>
public sealed record Wallet(
    [property: JsonPropertyName("divisorOleada")] int WaveDivisor,
    [property: JsonPropertyName("bonusPorGanar")] int WinBonus);

/// <summary>Los topes con los que se recorta lo que reclama el cliente.</summary>
public sealed record Limits(
    [property: JsonPropertyName("oleadaMaxima")] int MaxWave,
    [property: JsonPropertyName("areaMinima")] int MinArea,
    [property: JsonPropertyName("areaMaxima")] int MaxArea,
    [property: JsonPropertyName("maxLogBytes")] int MaxLogBytes,
    [property: JsonPropertyName("maxCuerpoBytes")] int MaxBodyBytes,
    [property: JsonPropertyName("maxTicksRevalidacion")] int MaxRevalidationTicks);

/// <summary>Un plano a la venta.</summary>
public sealed record BlueprintOnSale(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("precio")] int Price);

/// <summary>
/// Lee <c>GameData\economia.json</c> y <c>GameData\planos.json</c>.
/// </summary>
/// <remarks>
/// <para>⚠ <b>Si falta un archivo o un número, tira.</b> No hay tabla de reserva
/// compilada, y es la misma regla que en el cliente: un servidor que arranca con
/// valores inventados porque no encontró el archivo reparte oro que no es el que
/// nadie decidió, y eso se descubre en la billetera de un jugador semanas
/// después. Es infinitamente mejor no arrancar.</para>
///
/// <para>Los JSON <b>llevan comentarios</b> —se leen con
/// <see cref="JsonCommentHandling.Skip"/>— porque el porqué de cada número vive
/// al lado del número: un número de balance sin la explicación de por qué es ese
/// número es un número que nadie se anima a tocar.</para>
/// </remarks>
public static class ServerDataLoader
{
    private static readonly JsonSerializerOptions _opciones = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>La carpeta de datos al lado del ejecutable.</summary>
    /// <remarks>
    /// <c>AppContext.BaseDirectory</c> y no el directorio actual: un servicio de
    /// Windows arranca con el directorio actual en <c>C:\Windows\System32</c>, y
    /// ahí no está ninguno de los dos archivos.
    /// </remarks>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "GameData");

    public static ServerData Load(string carpeta)
    {
        var economia = Read<EconomyFile>(Path.Combine(carpeta, "economia.json"));
        var planos = Read<BlueprintsFile>(Path.Combine(carpeta, "planos.json"));

        var datos = new ServerData
        {
            Wallet = economia.Wallet ?? throw Missing("economia.json", "billetera"),
            Limits = economia.Limits ?? throw Missing("economia.json", "limites"),
            Blueprints = planos.Blueprints ?? throw Missing("planos.json", "planos"),
        };

        Verify(datos);

        return datos;
    }

    private static T Read<T>(string ruta)
    {
        if (!File.Exists(ruta))
        {
            throw new InvalidOperationException(
                $"Falta el archivo de datos '{ruta}'. Tiene que estar al lado del ejecutable: " +
                "revisá que la carpeta GameData\\ se esté copiando a la salida del build.");
        }

        try
        {
            // UTF-8 sin excepciones: los comentarios llevan tildes y eñes.
            string texto = File.ReadAllText(ruta, System.Text.Encoding.UTF8);

            return JsonSerializer.Deserialize<T>(texto, _opciones)
                   ?? throw new InvalidOperationException($"'{ruta}' está vacío.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"'{ruta}' no es JSON válido: {e.Message}", e);
        }
    }

    private static InvalidOperationException Missing(string archivo, string bloque)
        => new($"A '{archivo}' le falta el bloque \"{bloque}\".");

    /// <summary>
    /// Que los números tengan sentido, no solo que estén.
    /// </summary>
    /// <remarks>
    /// Un campo que falta se deserializa en <b>cero</b>, sin error y sin aviso, y
    /// un <c>divisorOleada</c> en cero es una división por cero en el primer
    /// <c>/run/finish</c> — tres horas después de que el problema se pudo haber
    /// visto.
    /// </remarks>
    private static void Verify(ServerData datos)
    {
        var problemas = new List<string>();

        if (datos.Wallet.WaveDivisor <= 0)
            problemas.Add("billetera.divisorOleada tiene que ser mayor que cero.");

        if (datos.Wallet.WinBonus < 0)
            problemas.Add("billetera.bonusPorGanar no puede ser negativo.");

        if (datos.Limits.MaxWave <= 0)
            problemas.Add("limites.oleadaMaxima tiene que ser mayor que cero.");

        if (datos.Limits.MinArea <= 0 || datos.Limits.MaxArea < datos.Limits.MinArea)
            problemas.Add("limites.areaMinima/areaMaxima no forman un rango válido.");

        if (datos.Limits.MaxLogBytes <= 0)
            problemas.Add("limites.maxLogBytes tiene que ser mayor que cero.");

        // ⚠ El cuerpo tiene que entrar por encima del log, con lugar para el
        // base64 (que infla ~33%). Al revés, Kestrel corta la conexión ANTES de
        // que el endpoint pueda recortar el log, y el jugador pierde la corrida
        // entera en vez de perder el final del log.
        if (datos.Limits.MaxBodyBytes <= datos.Limits.MaxLogBytes)
            problemas.Add("limites.maxCuerpoBytes tiene que ser mayor que maxLogBytes.");

        if (datos.Limits.MaxRevalidationTicks <= 0)
            problemas.Add("limites.maxTicksRevalidacion tiene que ser mayor que cero.");

        var vistas = new HashSet<string>(StringComparer.Ordinal);

        foreach (var plano in datos.Blueprints)
        {
            if (string.IsNullOrWhiteSpace(plano.Key))
                problemas.Add("hay un plano sin \"key\".");
            else if (!vistas.Add(plano.Key))
                problemas.Add($"el plano '{plano.Key}' está repetido.");

            if (plano.Price <= 0)
                problemas.Add($"el plano '{plano.Key}' tiene precio {plano.Price}: tiene que ser mayor que cero.");
        }

        if (problemas.Count > 0)
        {
            throw new InvalidOperationException(
                "Los datos del servidor no cierran:" + Environment.NewLine +
                string.Join(Environment.NewLine, problemas.Select(p => "  · " + p)));
        }
    }

    // Los dos archivos tal como salen del JSON. Van con propiedades anulables
    // para poder decir "falta el bloque X" en vez de reventar con un cero.
    //
    // ⚠ Los [JsonPropertyName] son la costura entre los dos idiomas: la propiedad
    // va en inglés como todo el código, y el atributo fija la clave que de verdad
    // está escrita en el archivo, que sigue en español. Sin ellos el binder
    // buscaría "wallet" donde el JSON dice "billetera" y el bloque llegaría en
    // null — o sea, un arranque abortado que culpa a un archivo que está bien.
    private sealed class EconomyFile
    {
        public int Version { get; init; }

        [JsonPropertyName("billetera")]
        public Wallet? Wallet { get; init; }

        [JsonPropertyName("limites")]
        public Limits? Limits { get; init; }
    }

    private sealed class BlueprintsFile
    {
        public int Version { get; init; }

        [JsonPropertyName("planos")]
        public BlueprintOnSale[]? Blueprints { get; init; }
    }
}
