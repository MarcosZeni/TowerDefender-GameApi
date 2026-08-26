using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MySqlConnector;
using TowerOfArgentum.Api.Config;
using TowerOfArgentum.Api.Data;
using TowerOfArgentum.Api.Endpoints;
using TowerOfArgentum.Api.Security;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// Orden de precedencia, de menor a mayor:
//
//   appsettings.json  <  appsettings.Local.json  <  variables de entorno  <  --flags
//
// En configuración gana el último que se registra, y ahí está la trampa:
// CreateBuilder ya dejó las variables de entorno y los flags arriba de todo, así
// que agregar un archivo DESPUÉS los tapa a los dos. Un `appsettings.Local.json`
// olvidado en el hosting haría que ConnectionStrings__Default se ignore en
// silencio y el servicio se conecte a la base equivocada.
//
// Por eso el archivo local se mete primero y las dos fuentes de arriba se vuelven
// a registrar: el entorno manda siempre, también en local.
//
// ⚠ En Linux el nombre distingue mayúsculas: `appsettings.local.json` en
// minúscula es otro archivo y se ignora sin decir nada.
// -----------------------------------------------------------------------------
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// ASPNETCORE_URLS necesita su propio registro, con el prefijo, porque el que lee
// Kestrel es la clave "Urls" pelada: sin quitar el prefijo, la variable entra
// como "ASPNETCORE_URLS" —una clave que no mira nadie— y el "Urls" de
// appsettings.json le gana por estar más abajo en la cadena. El síntoma es de los
// peores: pedís 0.0.0.0 para exponerla, arranca sin quejarse en 127.0.0.1, y la
// conexión de afuera sale rechazada como si fuera el firewall.
builder.Configuration.AddEnvironmentVariables("ASPNETCORE_");

builder.Configuration.AddCommandLine(args);

var config = builder.Configuration.Get<ApiConfig>() ?? new ApiConfig();

string connectionString = builder.Configuration.GetConnectionString("Default")
                          ?? config.ConnectionString;

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Falta la cadena de conexión: poné ConnectionStrings:Default en appsettings.Local.json " +
        "o la variable de entorno ConnectionStrings__Default.");
}

var database = new Database(connectionString);
var gate = new HashGate(config.Seguridad);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(config.Seguridad);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(gate);
builder.Services.AddSingleton(new PasswordHasher(config.Seguridad));
builder.Services.AddSingleton(new CuentaRepositorio(database));
builder.Services.AddSingleton(new TokenRepositorio(database));
builder.Services.AddSingleton(new RunRepositorio(database));
builder.Services.AddSingleton(new IntentoRepositorio(database));

// -----------------------------------------------------------------------------
// Rate limit por IP
//
// Es la razón de ser de esta capa: un script tirando logins deja a todos afuera.
// Rechazar acá cuesta prácticamente nada; lo caro es el hash de Argon2id, y a
// eso no se llega.
//
// Es instantáneo y no toca la base, que es lo que hace falta bajo una avalancha.
// Lo que NO puede hacer es acordarse: se reinicia la API y se olvida de todo, y
// con dos instancias cada una cuenta por su lado.
//
// De eso se ocupa la otra mitad, IntentoRepositorio, que cuenta sobre
// intentos_login adentro del endpoint de login. Mismo techo, dos lugares donde se
// aplica, y ninguno alcanza solo: sacar este convertiría cada pedido de una
// avalancha en una consulta a MySQL.
// -----------------------------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (contexto, ct) =>
    {
        contexto.HttpContext.Response.Headers.RetryAfter = "60";
        await contexto.HttpContext.Response.WriteAsJsonAsync(ErrorResponse.DemasiadosIntentos, ct);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: (http.Connection.RemoteIpAddress ?? IPAddress.None).ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = config.Seguridad.MaxIntentosPorIpPorMinuto,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // ⚠ 12 MB y no 4 KB, porque /run/finish manda el log de acciones de una
    // corrida de 100 oleadas: horas de juego, decenas de miles de acciones. El
    // endpoint recorta a 8 MB y marca `log_truncado`, así que este techo tiene
    // que estar por encima de ese, con lugar para el base64 (que infla ~33%).
    //
    // El número definitivo sale cuando haya un log real medido. Hasta entonces,
    // pecar de generoso: rechazar un finish después de tres horas bien jugadas es
    // lo peor que puede hacer este servidor.
    kestrel.Limits.MaxRequestBodySize = 12 * 1024 * 1024;
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxConcurrentConnections = config.Seguridad.MaxConexionesConcurrentes;

    // Contra slowloris: conexiones que abren y mandan los headers de a un byte
    // para ocupar un lugar sin gastar nada.
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

app.UseRateLimiter();

app.MapAuth();
app.MapRuns();
app.MapPerfil();

// El launcher y el monitoreo preguntan acá. Contestar "ok" sin saber si la base
// está viva no le sirve a nadie, así que se la pinguea.
app.MapGet("/health", async (Database db, ApiConfig cfg, CancellationToken ct) =>
{
    bool baseOk = await db.CanConnectAsync(ct);
    return Results.Json(new
    {
        estado = baseOk ? "ok" : "degradado",
        baseDatos = baseOk ? "ok" : "caida",
        datosVersion = cfg.DatosVersion,
    }, statusCode: baseOk ? 200 : 503);
});

var log = app.Services.GetRequiredService<ILogger<Program>>();

// A qué base se conectó, dicho en voz alta.
//
// Es la misma línea que imprime el LoginServer de Austral, y por el mismo
// motivo: cuando dos procesos miran bases distintas, el error que se ve no
// habla de la base. Acá el modo de fallar va a ser que el cliente abra una
// corrida contra una base y la cierre contra otra, y el mensaje sea "runId
// inválido" — que apunta al runId y no a lo que realmente pasa.
{
    var bd = new MySqlConnectionStringBuilder(connectionString);
    log.LogInformation(
        "Base '{Base}' en {Servidor}:{Puerto} (usuario {Usuario}) · datos v{Version}",
        bd.Database, bd.Server, bd.Port, bd.UserID, config.DatosVersion);
}

// Dónde quedó escuchando de verdad, que no siempre es lo que dice la config: la
// pisan ASPNETCORE_URLS y --urls. Sin esto, un puerto distinto al que espera el
// cliente se descubre recién cuando la conexión sale rechazada.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var direcciones = app.Services
        .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

    foreach (string direccion in direcciones?.Addresses ?? [])
        log.LogInformation("Escuchando en {Direccion}", direccion);
});

// -----------------------------------------------------------------------------
// La base tiene lo que hace falta
//
// Las migraciones se corren A MANO, una vez por base: no hay nada que las aplique
// solo. El modo de fallar es siempre el mismo —deploy nuevo, servicio arriba,
// base sin migrar— y sin este chequeo se descubre recién en el primer pedido de
// un jugador, como un 500 con un mensaje de MySQL que no dice qué hacer.
// -----------------------------------------------------------------------------
var schema = await new SchemaCheck(database).CorrerAsync(CancellationToken.None);

if (!schema.Alcanzable)
{
    // No se aborta: la API puede arrancar antes que MySQL, y de eso el servicio
    // se recupera solo. /health lo va a decir mientras tanto.
    log.LogWarning(
        "No se pudo verificar el schema porque la base no responde: {Error}. " +
        "La API arranca igual, pero ningún pedido va a funcionar hasta que MySQL vuelva.",
        schema.ErrorConexion);
}
else if (schema.Problemas.Count > 0)
{
    string detalle = string.Join(Environment.NewLine, schema.Problemas.Select(p => "  · " + p));

    log.LogCritical(
        "La base no tiene el schema que esta API necesita:{Detalle}",
        Environment.NewLine + detalle + Environment.NewLine + Environment.NewLine +
        "Aplicá las migraciones en orden, una por vez:" + Environment.NewLine +
        "    mysql -h 127.0.0.1 -P 46000 -u root -p towerdef < sql/000_base_y_usuario.sql" + Environment.NewLine +
        "    mysql -h 127.0.0.1 -P 46000 -u root -p towerdef < sql/001_cuentas.sql" + Environment.NewLine +
        "    ... y así con las demás, en orden numérico.");

    return 1;
}

app.Run();
return 0;

/// <summary>Marcador para <c>ILogger&lt;Program&gt;</c> con top-level statements.</summary>
public partial class Program;
