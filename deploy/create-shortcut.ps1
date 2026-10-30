<#
.SYNOPSIS
    Ярлык «PdmLite Explorer» на рабочем столе и в меню «Пуск».

.DESCRIPTION
    По умолчанию ярлык указывает на установленный Explorer (%LOCALAPPDATA%\PdmLite\Explorer),
    а если он не установлен — на сборку Visual Studio в этом репозитории (bin\Debug).
    Другой путь можно указать параметром -Target.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\create-shortcut.ps1
#>
param([string]$Target)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Target) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "PdmLite\Explorer\PdmLite.Explorer.exe"),
        (Join-Path $repo "src\PdmLite.Explorer\bin\Release\net8.0-windows\PdmLite.Explorer.exe"),
        (Join-Path $repo "src\PdmLite.Explorer\bin\Debug\net8.0-windows\PdmLite.Explorer.exe")
    )
    $Target = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Target -or -not (Test-Path $Target)) {
    throw "Не найден PdmLite.Explorer.exe. Соберите решение или установите PdmLite (deploy\install.ps1), либо укажите путь: -Target <путь к exe>."
}

$shell = New-Object -ComObject WScript.Shell
$places = @(
    [Environment]::GetFolderPath("Desktop"),
    (Join-Path ([Environment]::GetFolderPath("Programs")) "PdmLite")
)
foreach ($folder in $places) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $link = $shell.CreateShortcut((Join-Path $folder "PdmLite Explorer.lnk"))
    $link.TargetPath = $Target
    $link.WorkingDirectory = Split-Path -Parent $Target
    $link.IconLocation = "$Target,0"
    $link.Description = "PdmLite Explorer — проект и стандартная библиотека"
    $link.Save()
    Write-Host "Ярлык: $(Join-Path $folder 'PdmLite Explorer.lnk')"
}
Write-Host "Указывает на: $Target" -ForegroundColor Green
