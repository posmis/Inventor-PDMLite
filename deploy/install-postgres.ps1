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
    [ValidateNotNullOrEmpty()][string]$Database = "pdmlite",
    [ValidateNotNullOrEmpty()][string]$User = "pdmlite",
    [ValidateRange(1,65535)][int]$Port = 5432
)

$ErrorActionPreference = "Stop"

function Find-Psql {
    Get-ChildItem "$env:ProgramFiles\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Directory.Parent.Name -replace '\D', '') } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

. (Join-Path $PSScriptRoot 'installer\legacy-postgres-functions.ps1')

function Initialize-PdmPostgres([string]$Database, [string]$User, [int]$Port) {
    $superSecure = $null
    $super = $null
    $userPassword = $null
    $pointer = [IntPtr]::Zero
    try {
        $superSecure = Read-Host "Пароль администратора PostgreSQL (пользователь postgres)" -AsSecureString
        try {
            $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($superSecure)
            $super = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        } finally {
            if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $pointer = [IntPtr]::Zero }
        }
        if ([string]::IsNullOrWhiteSpace($super)) { throw 'Пароль администратора не может быть пустым.' }
        $psql = Find-Psql
        if (-not $psql) {
            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'Не найден winget. Установите PostgreSQL 16 вручную и запустите скрипт снова.' }
            Install-PdmPostgresPackage $super $Port
            $psql = Find-Psql
            if (-not $psql) { throw 'Установка завершилась, но psql.exe не найден.' }
        }
        $userPassword = New-PdmDatabasePassword
        $roleName = ConvertTo-PdmSqlIdentifier $User
        $databaseName = ConvertTo-PdmSqlIdentifier $Database
        $roleExists = Invoke-PdmSql $psql $Port $super ('SELECT 1 FROM pg_roles WHERE rolname = ' + (ConvertTo-PdmSqlLiteral $User))
        if ($roleExists -eq '1') { throw "Роль $User уже существует. Используйте другое имя роли; её пароль не изменён." }
        Invoke-PdmSql $psql $Port $super ('CREATE ROLE ' + $roleName + ' WITH LOGIN PASSWORD ' + (ConvertTo-PdmSqlLiteral $userPassword)) | Out-Null
        $dbExists = Invoke-PdmSql $psql $Port $super ('SELECT 1 FROM pg_database WHERE datname = ' + (ConvertTo-PdmSqlLiteral $Database))
        if ($dbExists -ne '1') { Invoke-PdmSql $psql $Port $super ('CREATE DATABASE ' + $databaseName + ' OWNER ' + $roleName + " ENCODING 'UTF8'") | Out-Null }
        Save-PdmDatabaseSettings $Database $User $Port $userPassword
        Write-Host "Готово: localhost:$Port / $Database / $User. Пароль приложения сохранён через DPAPI текущего пользователя." -ForegroundColor Green
    } finally {
        if ($superSecure) { $superSecure.Dispose() }
        $super = $null
        $userPassword = $null
    }
}

if ($MyInvocation.InvocationName -ne '.') { Initialize-PdmPostgres $Database $User $Port }
