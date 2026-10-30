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
if (Test-Path $addinOut) { Remove-Item $addinOut -Recurse -Force }
# Загрузчик — в папку плагина, сам плагин со всеми зависимостями — в её подпапку core.
dotnet publish "$repo\src\PdmLite.AddIn\PdmLite.AddIn.csproj" -c $Configuration -o $addinOut -p:PdmLiteRegisterAddIn=false
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки загрузчика плагина." }
dotnet publish "$repo\src\PdmLite.AddIn.Core\PdmLite.AddIn.Core.csproj" -c $Configuration -o (Join-Path $addinOut "core")
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки плагина." }

Write-Host "Сборка Explorer..." -ForegroundColor Cyan
dotnet publish "$repo\src\PdmLite.Explorer\PdmLite.Explorer.csproj" -c $Configuration -o $explorerOut
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки Explorer." }

Write-Host "Регистрация плагина в Inventor $InventorVersion..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null
$assembly = Join-Path $addinOut "PdmLite.AddIn.dll"
$manifest = (Get-Content "$PSScriptRoot\PdmLite.addin.template" -Raw -Encoding UTF8).Replace("{ASSEMBLY_PATH}", $assembly)
[System.IO.File]::WriteAllText((Join-Path $manifestDir "PdmLite.addin"), $manifest, (New-Object System.Text.UTF8Encoding $false))

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
Write-Host "  Плагин:   $addinOut"
Write-Host "  Explorer: $explorerOut\PdmLite.Explorer.exe"
Write-Host "  Манифест: $manifestDir\PdmLite.addin"
Write-Host "  Журналы:  $target\logs"
