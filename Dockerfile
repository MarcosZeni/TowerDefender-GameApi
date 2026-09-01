# =============================================================================
#  La imagen del GameApi para el hosting (Railway o cualquier Docker).
#
#  Dos etapas: se compila con el SDK y se corre con el runtime pelado de
#  ASP.NET, que pesa un cuarto. El clon es autocontenido —el .nupkg de la
#  simulación viaja en paquetes\ dentro del repo, ver nuget.config— así que
#  no hay pasos previos: docker build y listo.
#
#  Lo que la imagen ESPERA del entorno, todo por variables:
#
#      PORT        lo inyecta Railway; la API escucha en 0.0.0.0:$PORT
#                  (ver Program.cs). Sin PORT, cae al Urls de appsettings.
#      MYSQL_URL   mysql://usuario:contraseña@host:puerto/base?sslMode=Required
#                  La contraseña va percent-encoded si trae @ : / ? #.
#
#  ⚠ GameData\ viaja adentro de la imagen y NO es configurable por entorno, a
#    propósito: es BALANCE, no config de despliegue — el mismo número en todas
#    las máquinas, con el porqué escrito al lado. Cambiarlo es redesplegar.
#
#  Probar en local:
#      docker build -t towerofargentum-api .
#      docker run -e PORT=5090 -e MYSQL_URL=mysql://... -p 5090:5090 towerofargentum-api
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Primero lo que cambia poco, para que la caché de capas trabaje: el restore se
# rehace solo cuando cambian el .csproj, el nuget.config o el paquete local.
COPY TowerOfArgentum.Api.csproj nuget.config ./
COPY paquetes/ paquetes/
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .

# Los números que decide el servidor. Sin estos archivos la API NO ARRANCA — no
# hay tabla de reserva compilada, y es a propósito (ver Config/ServerData.cs).
COPY --from=build /src/GameData/ GameData/

ENTRYPOINT ["dotnet", "TowerOfArgentum.Api.dll"]
