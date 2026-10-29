<div align="center">

# Tower of Argentum — GameApi

**El servidor del juego: cuentas, corridas, ranking y billetera. Nunca simula; verifica.**
*Minimal API in .NET 10 that hands out seeds, stores runs, serves the global ranking and re-simulates claims to validate them.*

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Minimal API](https://img.shields.io/badge/ASP.NET-minimal%20API-5C2D91)
![MySQL 8](https://img.shields.io/badge/MySQL-8-4479A1?logo=mysql&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-listo-2496ED?logo=docker&logoColor=white)

</div>

---

## Qué hace

La partida de [Tower of Argentum](https://github.com/TowerDefenderAO/Client) corre **entera en el cliente**. Este servidor hace lo único que el cliente no puede hacer por sí solo:

- **Reparte la seed.** `POST /run/start` crea la corrida y devuelve `runId` + seed generada acá. Sin un `runId` abierto no se acepta ningún resultado.
- **Guarda el reclamo.** `POST /run/finish` recibe hasta dónde llegó el jugador y el **log de acciones** (solo decisiones: poner, vender, equipar, comprar). Entra a la base con `validado = 0`.
- **Lo verifica.** Como la simulación es determinista y la seed es del servidor, puede **re-simular la corrida con el mismo paquete que usa el cliente** ([`TowerOfArgentum.Simulation`](https://github.com/TowerDefenderAO/Client/tree/main/Simulation)) y comparar oleada y ticks con lo reclamado.
- **Sirve el ranking global** (`ganó → oleada → ticks`) y la **billetera**, la única progresión permanente: el oro de cada corrida lo deriva el servidor, y con él se compran planos de fusión.

No hay sockets, ni websockets, ni estado en tiempo real. Todo es HTTP/JSON.

## Endpoints

| Método | Ruta | Qué hace | Token |
|---|---|---|---|
| `POST` | `/auth/registro` | Crea la cuenta (nick + contraseña, email opcional) | — |
| `POST` | `/auth/login` | Devuelve un token opaco de 32 bytes | — |
| `POST` | `/auth/logout` | Invalida el token | ✔ |
| `POST` | `/run/start` | Abre una corrida: `runId` + seed. Verifica la versión y el hash de los datos del cliente | ✔ |
| `POST` | `/run/progress` | Checkpoint en la nube: oleada, ticks y log hasta acá | ✔ |
| `GET` | `/run/actual` | La corrida abierta, para retomarla desde otra máquina | ✔ |
| `POST` | `/run/finish` | Cierra la corrida con el reclamo y el log. Acredita el oro | ✔ |
| `GET` | `/ranking` | Tabla global, por temporada de datos | — |
| `GET` | `/perfil/me` | Cuenta, billetera y planos aprendidos | ✔ |
| `GET` | `/tienda/planos` | Qué planos se venden y a cuánto | — |
| `POST` | `/tienda/plano` | Compra un plano con la billetera. Única y permanente | ✔ |
| `GET` | `/health` | Estado del servicio y de la base | — |

El contrato completo, con los JSON de cada pedido y cada error, está en [Doc/Servidor/01-endpoints.md](https://github.com/TowerDefenderAO/Doc/blob/main/Servidor/01-endpoints.md).

## Decisiones de seguridad

- **Contraseñas con Argon2id** (`m=19456, t=2, p=1`, formato PHC) detrás de un semáforo de hashes, y tiempo constante en los fallos de login.
- **Tokens opacos** de 32 bytes de CSPRNG; en la base se guarda solo su SHA-256.
- **Rate limiting por IP** (ventana fija de un minuto) y **freno por nick** a los intentos de login fallidos (`429`).
- **El cliente nunca manda oro ni precios.** `/run/finish` reporta hasta dónde llegó; el oro lo calcula el servidor. `/tienda/plano` recibe solo la clave del plano; el precio sale de `GameData/planos.json`.
- **Un hash por temporada.** El cliente manda la versión y el hash de sus datos de balance al abrir la corrida; si no coinciden con los del servidor, la corrida no abre. Así las corridas de una temporada no se mezclan con otra.
- **Lo que manda el cliente es un reclamo, no un hecho.** Entra con `validado = 0`, y el [`Revalidator`](Data/Revalidator.cs) lo confirma re-simulando. Un resultado distinto no prueba trampa por sí solo: devuelve el detalle, y decide una persona.
- **Usuario de MySQL con permisos mínimos**: `SELECT, INSERT, UPDATE, DELETE` sobre una sola base. Sin `DROP`, sin `GRANT`, sin `FILE`.
- **Ningún secreto en el repo.** `appsettings.json` se versiona con la cadena de conexión vacía; la real va en `appsettings.Local.json` (ignorado) o en variables de entorno.

## Configuración

Precedencia, de menor a mayor: `appsettings.json` < `appsettings.Local.json` < variables de entorno < flags de línea de comandos.

| Variable | Para qué |
|---|---|
| `ConnectionStrings__Default` | Cadena clásica de MySqlConnector (`Server=...;Port=...;Database=...;User ID=...;Password=...`) |
| `MYSQL_URL` | Alternativa en formato URL, el que reparten los hostings: `mysql://usuario:contraseña@host:puerto/base?sslMode=Required` |
| `PORT` | Puerto de escucha (Railway, Render y compañía lo inyectan). Sin él se usa `Urls` del `appsettings.json` |
| `ASPNETCORE_URLS` | Si está, gana sobre todo lo demás |

Los **números que decide el servidor** —precio de cada plano, oro por corrida, topes del reclamo— no están en el código ni en `appsettings.json`: viven en [GameData/planos.json](GameData/planos.json) y [GameData/economia.json](GameData/economia.json), con el porqué de cada número comentado al lado. Sin esos archivos la API no arranca.

## Correr en local

1. Un **MySQL 8** con una base `utf8mb4`. El esquema está en [sql/DBFull.sql](sql/DBFull.sql):
   ```bash
   mysql -h 127.0.0.1 -P 3306 -u root -p < sql/DBFull.sql
   ```
2. Copiar `appsettings.Local.json.ejemplo` a `appsettings.Local.json` y completar la cadena de conexión.
3. Levantar:
   ```bash
   dotnet run
   ```
   Al arrancar imprime a qué base se conectó y en qué puerto escucha. `GET /health` responde `{"estado":"ok","baseDatos":"ok","datosVersion":N}`.

### Con Docker

```bash
docker build -t towerofargentum-api .
docker run -e PORT=5090 -e MYSQL_URL=mysql://usuario:contraseña@host:3306/towerdef -p 5090:5090 towerofargentum-api
```

La imagen es de dos etapas (SDK para compilar, runtime de ASP.NET para correr) y el clon es autocontenido: el paquete de la simulación viaja en [paquetes/](paquetes/) dentro del repo, así que no hace falta la carpeta hermana del cliente para compilar.

## Estructura

| Carpeta | Qué hay |
|---|---|
| [Endpoints/](Endpoints/) | Un archivo estático por grupo de rutas: auth, runs, perfil/ranking/tienda. Respuestas de error con códigos estables |
| [Security/](Security/) | Argon2id, semáforo de hashes, token opaco, autenticación por header |
| [Data/](Data/) | Repositorios con SQL en raw strings y parámetros, chequeo de esquema al arrancar, y el revalidador |
| [Config/](Config/) | Config tipada: API, seguridad, y los datos del servidor |
| [GameData/](GameData/) | Los números del servidor, con comentarios |
| [sql/](sql/) | El esquema completo y el script para levantar una instancia de MySQL dedicada |

## Convenciones

- **Identificadores en inglés, prosa en español.** Los campos de la API y las columnas de la base quedaron en español porque son un contrato ya desplegado; la costura entre los dos idiomas son los `[JsonPropertyName]`.
- **Los números van al JSON, las fórmulas al código.** Igual que en el cliente.
- **Migraciones a mano**, idempotentes, con el porqué en prosa al principio y un comentario por columna. Sin ORM, sin migraciones automáticas.

Los repos hermanos: [Client](https://github.com/TowerDefenderAO/Client) (el juego), [Resources](https://github.com/TowerDefenderAO/Resources) (el arte) y [Doc](https://github.com/TowerDefenderAO/Doc) (el diseño y el porqué de todo).
