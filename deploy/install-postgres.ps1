<#
.SYNOPSIS
    Установка PostgreSQL для PdmLite на этот компьютер и настройка подключения.

.DESCRIPTION
    1. Если PostgreSQL не установлен — ставит PostgreSQL 16 через winget (тихая установка).
    2. Создаёт пользователя и базу PdmLite (по умолчанию pdmlite / pdmlite).
    3. Записывает подключение в %APPDATA%\PdmLite\database.json; пароль пользователя pdmlite
       генерируется случайно и хранится зашифрованным для текущего пользователя Windows.

    Скрипт спросит пароль администратора PostgreSQL (пользователь postgres):
    - при новой установке — придумайте и запомните его;
    - если PostgreSQL уже установлен — введите существующий.

    Таблицы PdmLite создаёт сам при первом подключении.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\install-postgres.ps1
#>
param(
    [string]$Database = "pdmlite",
    [string]$User = "pdmlite",
    [int]$Port = 5432
)

$ErrorActionPreference = "Stop"

function Find-Psql {
    Get-ChildItem "$env:ProgramFiles\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Directory.Parent.Name -replace '\D', '') } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

$superSecure = Read-Host "Пароль администратора PostgreSQL (пользователь postgres)" -AsSecureString
$super = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($superSecure))
if ([string]::IsNullOrWhiteSpace($super)) { throw "Пароль администратора не может быть пустым." }

# 1. Установка
$psql = Find-Psql
if (-not $psql) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Не найден winget. Установите PostgreSQL 16 вручную (https://www.postgresql.org/download/windows/), затем запустите скрипт снова."
    }
    Write-Host "Установка PostgreSQL 16 (несколько минут)..." -ForegroundColor Cyan
    $override = "--mode unattended --unattendedmodeui minimal --superpassword `"$super`" --serverport $Port --disable-components stackbuilder"
    winget install --id PostgreSQL.PostgreSQL.16 --exact --silent --accept-source-agreements --accept-package-agreements --override $override
    if ($LASTEXITCODE -ne 0) { throw "Установка PostgreSQL не удалась (код $LASTEXITCODE)." }
    $psql = Find-Psql
    if (-not $psql) { throw "PostgreSQL установлен, но psql.exe не найден в $env:ProgramFiles\PostgreSQL." }
} else {
    Write-Host "PostgreSQL уже установлен: $psql" -ForegroundColor Cyan
}

# 2. Пользователь и база
$env:PGPASSWORD = $super
function Invoke-Sql([string]$sql) {
    $result = & $psql -h localhost -p $Port -U postgres -d postgres -v ON_ERROR_STOP=1 -tA -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Ошибка PostgreSQL: $result`nПроверьте пароль администратора и что служба PostgreSQL запущена."
    }
    return $result
}

$chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
$userPassword = -join (1..24 | ForEach-Object { $chars | Get-Random })

Write-Host "Создание пользователя и базы $Database..." -ForegroundColor Cyan
$roleExists = Invoke-Sql ("SELECT 1 FROM pg_roles WHERE rolname = '{0}'" -f $User)
if ($roleExists -eq "1") {
    throw "Роль $User уже существует. Используйте другое имя роли; её пароль не изменён."
} else {
    Invoke-Sql ("CREATE ROLE ""{0}"" WITH LOGIN PASSWORD '{1}'" -f $User, $userPassword) | Out-Null
}
$dbExists = Invoke-Sql ("SELECT 1 FROM pg_database WHERE datname = '{0}'" -f $Database)
if ($dbExists -ne "1") {
    Invoke-Sql ("CREATE DATABASE ""{0}"" OWNER ""{1}"" ENCODING 'UTF8'" -f $Database, $User) | Out-Null
}
Remove-Item Env:PGPASSWORD

# 3. Подключение для PdmLite (пароль — DPAPI текущего пользователя, как ConvertFrom-SecureString)
$settingsDir = Join-Path $env:APPDATA "PdmLite"
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
$protected = ConvertFrom-SecureString (ConvertTo-SecureString $userPassword -AsPlainText -Force)
$settings = [ordered]@{
    Host = "localhost"
    Port = $Port
    Database = $Database
    Username = $User
    ProtectedPassword = $protected
}
$json = $settings | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $settingsDir "database.json"), $json, (New-Object System.Text.UTF8Encoding $false))

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
Write-Host "  Сервер:       localhost:$Port"
Write-Host "  База:         $Database"
Write-Host "  Пользователь: $User (пароль сохранён в $settingsDir\database.json, зашифрован)"
Write-Host ""
Write-Host "Проверка: PdmLite Explorer -> Сервис -> Настройки библиотеки -> Проверить подключение."
