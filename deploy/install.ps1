<#
.SYNOPSIS
    Сборка и установка PdmLite для текущего пользователя.

.DESCRIPTION
    - плагин:    %LOCALAPPDATA%\PdmLite\AddIn
    - Explorer:  %LOCALAPPDATA%\PdmLite\Explorer
    - манифест:  %APPDATA%\Autodesk\Inventor 2025\Addins\PdmLite.addin

    Требуется .NET 8 SDK и установленный Inventor 2025. Inventor должен быть закрыт.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\install.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$InventorVersion = "2025"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA "PdmLite"
$addinOut = Join-Path $target "AddIn"
$explorerOut = Join-Path $target "Explorer"
$manifestDir = Join-Path $env:APPDATA "Autodesk\Inventor $InventorVersion\Addins"

if (Get-Process -Name Inventor -ErrorAction SilentlyContinue) {
    throw "Закройте Inventor перед установкой: файлы плагина заняты."
}
if (Get-Process -Name PdmLite.Explorer -ErrorAction SilentlyContinue) {
    throw "Закройте PdmLite Explorer перед установкой."
}

Write-Host "Сборка плагина..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $target | Out-Null
$stage = Join-Path $target ("install-" + [guid]::NewGuid().ToString('N'))
$stagedAddin = Join-Path $stage "AddIn"
$stagedExplorer = Join-Path $stage "Explorer"
# Загрузчик — в папку плагина, сам плагин со всеми зависимостями — в её подпапку core.
dotnet publish "$repo\src\PdmLite.AddIn\PdmLite.AddIn.csproj" -c $Configuration -o $stagedAddin -p:PdmLiteRegisterAddIn=false
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки загрузчика плагина." }
dotnet publish "$repo\src\PdmLite.AddIn.Core\PdmLite.AddIn.Core.csproj" -c $Configuration -o (Join-Path $stagedAddin "core")
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки плагина." }

Write-Host "Сборка Explorer..." -ForegroundColor Cyan
dotnet publish "$repo\src\PdmLite.Explorer\PdmLite.Explorer.csproj" -c $Configuration -o $stagedExplorer
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки Explorer." }

New-Item -ItemType Directory -Force -Path $target | Out-Null
$oldAddin = Join-Path $stage "AddIn-old"
$oldExplorer = Join-Path $stage "Explorer-old"
try {
    if (Test-Path $addinOut) { Move-Item $addinOut $oldAddin }
    if (Test-Path $explorerOut) { Move-Item $explorerOut $oldExplorer }
    Move-Item $stagedAddin $addinOut
    Move-Item $stagedExplorer $explorerOut
} catch {
    if (Test-Path $oldAddin) { Remove-Item $addinOut -Recurse -Force -ErrorAction SilentlyContinue; Move-Item $oldAddin $addinOut }
    if (Test-Path $oldExplorer) { Remove-Item $explorerOut -Recurse -Force -ErrorAction SilentlyContinue; Move-Item $oldExplorer $explorerOut }
    throw
}
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Регистрация плагина в Inventor $InventorVersion..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null
$assembly = Join-Path $addinOut "PdmLite.AddIn.dll"
$manifest = (Get-Content "$PSScriptRoot\PdmLite.addin.template" -Raw -Encoding UTF8).Replace("{ASSEMBLY_PATH}", $assembly)
[System.IO.File]::WriteAllText((Join-Path $manifestDir "PdmLite.addin"), $manifest, (New-Object System.Text.UTF8Encoding $false))

& "$PSScriptRoot\create-shortcut.ps1" -Target (Join-Path $explorerOut "PdmLite.Explorer.exe")

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
Write-Host "  Плагин:   $addinOut"
Write-Host "  Explorer: $explorerOut\PdmLite.Explorer.exe"
Write-Host "  Манифест: $manifestDir\PdmLite.addin"
Write-Host "  Журналы:  $target\logs"
