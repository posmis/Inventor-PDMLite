<#
.SYNOPSIS
    Builds selected PdmLite 0.1 installers on Windows with Inventor 2025.
.EXAMPLE
    .\deploy\build-release.ps1
.EXAMPLE
    .\deploy\build-release.ps1 -LibraryBackup 'D:\Library.zip'
#>
param(
    [ValidateSet('Online','Offline','Both')][string]$Variant = 'Both',
    [string]$LibraryBackup = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PdmLite-Library-ver-0.1.zip'),
    [string]$IsccPath,
    [string]$PostgreSqlInstaller,
    [string]$InventorInteropPath = (Join-Path $env:ProgramFiles 'Autodesk\Inventor 2025\Bin\Public Assemblies\Autodesk.Inventor.Interop.dll')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo ('artifacts\installer\' + $Variant)
$payload = Join-Path $build 'payload'
$output = Join-Path $repo 'artifacts\release'
$pg = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer\postgresql.json') -Raw | ConvertFrom-Json

if (-not (Test-Path -LiteralPath $LibraryBackup -PathType Leaf)) { throw "Не найден архив библиотеки: $LibraryBackup" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Установите .NET 8 SDK x64: https://dotnet.microsoft.com/download/dotnet/8.0' }
if (-not (Test-Path -LiteralPath $InventorInteropPath)) { throw "Не найден Inventor 2025 API: $InventorInteropPath" }
if (Get-Process -Name Inventor,PdmLite.Explorer -ErrorAction SilentlyContinue) { throw 'Закройте Inventor и PdmLite Explorer.' }
. (Join-Path $PSScriptRoot 'installer\find-inno-setup.ps1')
$IsccPath = Find-InnoSetupCompiler -ExplicitPath $IsccPath
if (-not $IsccPath) {
    throw 'Не удалось найти ISCC.exe установленного Inno Setup. Укажите путь вручную: .\deploy\build-release.ps1 -IsccPath "C:\папка Inno Setup\ISCC.exe". Требуется Inno Setup 6.3 или новее.'
}
Write-Host "Inno Setup: $IsccPath" -ForegroundColor Cyan

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $LibraryBackup).Path)
try {
    if (-not $zip.GetEntry('manifest.json') -or -not $zip.GetEntry('data/library.json')) { throw 'Требуется ZIP резервной копии PdmLite, а не обычная папка с деталями.' }
} finally { $zip.Dispose() }

New-Item -ItemType Directory -Force -Path $build,$output | Out-Null
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'Setup') | Out-Null

function Publish-PdmLite([string]$Project, [string]$Destination, [bool]$Standalone) {
    $arguments = @('publish', (Join-Path $repo $Project), '-c', 'Release', '-r', 'win-x64', '--self-contained', $Standalone.ToString().ToLowerInvariant(), '-o', $Destination,
        '-p:Version=0.1.0', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PdmLiteRegisterAddIn=false', "-p:InventorInteropPath=$InventorInteropPath")
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать $Project" }
}
Write-Host 'Сборка готовых приложений...' -ForegroundColor Cyan
Publish-PdmLite 'src\PdmLite.AddIn\PdmLite.AddIn.csproj' (Join-Path $payload 'AddIn') $false
Publish-PdmLite 'src\PdmLite.AddIn.Core\PdmLite.AddIn.Core.csproj' (Join-Path $payload 'AddIn\core') $false
Publish-PdmLite 'src\PdmLite.Explorer\PdmLite.Explorer.csproj' (Join-Path $payload 'Explorer') $true
Publish-PdmLite 'tools\PdmLite.Setup\PdmLite.Setup.csproj' (Join-Path $payload 'Setup') $true
# Apprentice must resolve the API also outside the Inventor process.
Copy-Item -LiteralPath $InventorInteropPath -Destination (Join-Path $payload 'Explorer\Autodesk.Inventor.Interop.dll')
Copy-Item -LiteralPath $LibraryBackup -Destination (Join-Path $payload 'Setup\library.zip')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PdmLite.addin.template') -Destination (Join-Path $payload 'Setup')
Copy-Item -Path (Join-Path $PSScriptRoot 'installer\install-postgresql-machine.ps1'),(Join-Path $PSScriptRoot 'installer\postgresql.json') -Destination (Join-Path $payload 'Setup')

# Online builds do not need a PostgreSQL download on the build computer.
if ($Variant -ne 'Online') {
    # A local EXE is accepted only with the same pinned hash.
    $pgFile = Join-Path $payload 'Setup\postgresql.exe'
    if ($PostgreSqlInstaller) { Copy-Item -LiteralPath $PostgreSqlInstaller -Destination $pgFile }
    else {
        Write-Host 'Загрузка PostgreSQL для автономного установщика...' -ForegroundColor Cyan
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $pg.Url -OutFile $pgFile -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $pgFile -Algorithm SHA256).Hash -ne $pg.Sha256) { throw 'Контрольная сумма PostgreSQL не совпала. Сборка остановлена.' }
}
$variants = if ($Variant -eq 'Both') { @('Online','Offline') } else { @($Variant) }
foreach ($installerVariant in $variants) {
    & $IsccPath "/DPayloadDir=$payload" "/DOutputDir=$output" "/DVariant=$installerVariant" (Join-Path $PSScriptRoot 'installer\PdmLite.iss')
    if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать установщик $installerVariant" }
}
. (Join-Path $PSScriptRoot 'installer\release-hashes.ps1')
$hashes = @(Write-ReleaseHashes -OutputDirectory $output -Variants $variants)
Write-Host 'Установщики текущего запуска:' -ForegroundColor Cyan
$hashes | ForEach-Object { Write-Host $_.Path }
Write-Host "Готово: $output" -ForegroundColor Green
Start-Process explorer.exe -ArgumentList ('"{0}"' -f $output)
