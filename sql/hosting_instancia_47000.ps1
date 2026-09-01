# =============================================================================
#  Segunda instancia de MySQL en el hosting WINDOWS, puerto 47000.
#
#  Detecta dónde está instalado MySQL (por los servicios que ya corren), arma
#  una instancia NUEVA con su datadir propio —no toca la del 3306—, la registra
#  como servicio de Windows con arranque automático, le pone contraseña a root
#  y crea la base `towerdef` vacía.
#
#  Es el mismo esquema que la PC de desarrollo: servicio MySQL47000, puerto 47000,
#  datadir aparte. Correr en el VPS, en una PowerShell COMO ADMINISTRADOR:
#
#      powershell -ExecutionPolicy Bypass -File .\hosting_instancia_47000.ps1
#
#  Al terminar imprime cómo conectarse. El firewall lo abrís vos después.
#  Las queries (hosting_setup.sql, DBFull.sql) las corrés vos adentro, con -P 47000.
# =============================================================================

$ErrorActionPreference = 'Stop'

$Puerto     = 47000
$Servicio   = 'MySQL47000'
$DataDir    = 'C:\MySQL\data47000'
$IniFile    = 'C:\MySQL\my-47000.ini'
$LogErr     = 'C:\MySQL\mysql-47000.err'
$ClaveFile  = 'C:\MySQL\clave-root-47000.txt'

function Fallo($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }
function Ok($msg)    { Write-Host $msg -ForegroundColor Green }

# --- Guardas -----------------------------------------------------------------
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) { Fallo 'Correlo como Administrador.' }

$enUso = Get-NetTCPConnection -LocalPort $Puerto -State Listen -ErrorAction SilentlyContinue
if ($enUso) { Fallo "Ya hay algo escuchando en el $Puerto. No toco nada." }

if (Get-Service $Servicio -ErrorAction SilentlyContinue) {
    Fallo "El servicio $Servicio ya existe. No lo piso."
}

if ((Test-Path $DataDir) -and (Get-ChildItem $DataDir -ErrorAction SilentlyContinue)) {
    Fallo "$DataDir ya existe y no está vacío. Borralo vos si es basura."
}

# --- 1. Encontrar el binario -------------------------------------------------
# ⚠ Primero la ruta FIJA del 8.0. En este VPS conviven el 5.7 y el 8.0, y la
#   detección por servicios agarraría el que corre el 3306 — que puede ser el
#   5.7, y el 5.7 no sirve: el dump usa utf8mb4_0900_ai_ci, que es de 8.0.
$mysqld = $null

$preferido = "$env:ProgramFiles\MySQL\MySQL Server 8.0\bin\mysqld.exe"
if (Test-Path $preferido) { $mysqld = $preferido }

if (-not $mysqld) {
    $serviciosMysql = Get-CimInstance Win32_Service |
        Where-Object { $_.PathName -match 'mysqld' }

    foreach ($s in $serviciosMysql) {
        # El PathName viene como: "C:\ruta\mysqld.exe" --defaults-file=... NOMBRE
        if ($s.PathName -match '"([^"]+mysqld\.exe)"') {
            $mysqld = $Matches[1]
            Write-Host "Servicio existente: $($s.Name) -> $mysqld"
            break
        }
    }
}

if (-not $mysqld) {
    # Sin servicios que lo delaten: rutas típicas.
    $candidatos = @(
        "$env:ProgramFiles\MySQL\MySQL Server 8.4\bin\mysqld.exe",
        "$env:ProgramFiles\MySQL\MySQL Server 8.0\bin\mysqld.exe",
        "$env:ProgramFiles\MySQL\MySQL Server 5.7\bin\mysqld.exe",
        'C:\xampp\mysql\bin\mysqld.exe'
    )
    foreach ($c in $candidatos) {
        if (Test-Path $c) { $mysqld = $c; break }
    }
}

if (-not $mysqld) { Fallo 'No encontré mysqld.exe. ¿Dónde está instalado MySQL?' }

$bin      = Split-Path $mysqld
$mysqlCli = Join-Path $bin 'mysql.exe'
if (-not (Test-Path $mysqlCli)) { Fallo "Está mysqld pero no el cliente: falta $mysqlCli" }

$version = (& $mysqld --version) 2>&1 | Out-String
Write-Host "Binario : $mysqld"
Write-Host "Versión : $($version.Trim())"

if ($version -match 'Ver 5\.') {
    Fallo 'Ese mysqld es 5.x y el dump necesita 8.0 (utf8mb4_0900_ai_ci no existe en 5.7).'
}

if ($version -match 'Maria') {
    Write-Host '⚠ Es MariaDB: DBFull.sql usa utf8mb4_0900_ai_ci, que MariaDB no tiene.' -ForegroundColor Yellow
    Write-Host '  El import va a fallar. Avisá antes de seguir.' -ForegroundColor Yellow
    exit 1
}

# --- 2. Carpetas y config ----------------------------------------------------
New-Item -ItemType Directory -Force $DataDir | Out-Null
New-Item -ItemType Directory -Force (Split-Path $IniFile) | Out-Null

# El ini de ESTA instancia. mysqlx apagado: el 33060 ya lo usa la del 3306.
@"
# Segunda instancia, solo para la base towerdef. La del 3306 no se toca.
[mysqld]
port=$Puerto
datadir=$($DataDir -replace '\\','/')
log-error=$($LogErr -replace '\\','/')
bind-address=0.0.0.0
character-set-server=utf8mb4
mysqlx=OFF
# Modesta a propósito: convive con la web y la otra instancia.
innodb_buffer_pool_size=256M
"@ | Out-File -FilePath $IniFile -Encoding ascii

# --- 3. Inicializar el datadir -----------------------------------------------
Write-Host "Inicializando $DataDir ..."
& $mysqld --defaults-file=$IniFile --initialize-insecure
if ($LASTEXITCODE -ne 0) { Fallo "La inicialización falló. Mirá el log: $LogErr" }

# --- 4. Registrar y arrancar el servicio -------------------------------------
& $mysqld --install $Servicio --defaults-file=$IniFile
if ($LASTEXITCODE -ne 0) { Fallo 'No se pudo registrar el servicio.' }

sc.exe config $Servicio start= auto | Out-Null
Start-Service $Servicio

# --- 5. Esperar a que atienda ------------------------------------------------
$listo = $false
foreach ($i in 1..30) {
    & $mysqlCli -h 127.0.0.1 -P $Puerto -u root --skip-password -e 'SELECT 1' 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $listo = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $listo) { Fallo "No levantó. Mirá el log: Get-Content $LogErr -Tail 50" }

# --- 6. Contraseña de root y la base -----------------------------------------
$alfabeto = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$claveRoot = -join ($bytes | ForEach-Object { $alfabeto[$_ % $alfabeto.Length] })

$sql = @"
ALTER USER 'root'@'localhost' IDENTIFIED BY '$claveRoot';
CREATE DATABASE IF NOT EXISTS towerdef CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
FLUSH PRIVILEGES;
"@
$sql | & $mysqlCli -h 127.0.0.1 -P $Puerto -u root --skip-password
if ($LASTEXITCODE -ne 0) { Fallo 'No pude poner la contraseña de root.' }

# La clave queda en un archivo que solo Administradores pueden leer.
$claveRoot | Out-File -FilePath $ClaveFile -Encoding ascii
icacls $ClaveFile /inheritance:r /grant:r 'Administradores:R' /grant:r 'Administrators:R' 2>$null | Out-Null

# --- 7. Resumen ---------------------------------------------------------------
Ok '─────────────────────────────────────────────────────────────'
Ok "Instancia lista: servicio $Servicio en el puerto $Puerto · base 'towerdef' creada"
Ok '─────────────────────────────────────────────────────────────'
Write-Host "root (solo localhost) : contraseña en $ClaveFile"
Write-Host "conectarse            : `"$mysqlCli`" -h 127.0.0.1 -P $Puerto -u root -p"
Write-Host "log de errores        : $LogErr"
Write-Host "servicio              : Get-Service $Servicio  (arranque automático)"
Write-Host ''
Write-Host 'Te queda a vos:'
Write-Host "  1. Abrir el $Puerto en el firewall:"
Write-Host "       New-NetFirewallRule -DisplayName 'MySQL towerdef' -Direction Inbound -Protocol TCP -LocalPort $Puerto -Action Allow"
Write-Host '  2. Correr adentro hosting_setup.sql y DBFull.sql (desde cmd, no PowerShell):'
Write-Host "       `"$mysqlCli`" -h 127.0.0.1 -P $Puerto -u root -p < hosting_setup.sql"
Write-Host "       `"$mysqlCli`" -h 127.0.0.1 -P $Puerto -u root -p < DBFull.sql"
