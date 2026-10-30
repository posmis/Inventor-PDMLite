<#
.SYNOPSIS
    Удаление PdmLite текущего пользователя (настройки в %APPDATA%\PdmLite сохраняются).
#>
param([string]$InventorVersion = "2025")

$ErrorActionPreference = "Stop"
$manifest = Join-Path $env:APPDATA "Autodesk\Inventor $InventorVersion\Addins\PdmLite.addin"
$target = Join-Path $env:LOCALAPPDATA "PdmLite"

if (Get-Process -Name Inventor -ErrorAction SilentlyContinue) {
    throw "Закройте Inventor перед удалением."
}

Remove-Item $manifest -ErrorAction SilentlyContinue
Remove-Item (Join-Path $target "AddIn") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $target "Explorer") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "PdmLite удалён." -ForegroundColor Green
