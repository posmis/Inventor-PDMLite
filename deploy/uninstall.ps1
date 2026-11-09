<#
.SYNOPSIS
    Удаление ручной установки PdmLite (deploy/install.ps1). Пользовательские данные сохраняются.
#>
param([string]$InventorVersion = '2025')
$ErrorActionPreference = 'Stop'

function Uninstall-PdmLite([string]$AppRoot, [string]$Manifest, [string[]]$Shortcuts) {
    # Check both hosts before changing any files or plugin registration.
    if (Get-Process -Name Inventor,PdmLite.Explorer -ErrorAction SilentlyContinue) {
        throw 'Закройте Inventor и PdmLite Explorer перед удалением.'
    }
    if (Get-ChildItem -LiteralPath $AppRoot -Filter 'unins*.exe' -ErrorAction SilentlyContinue) {
        throw 'PdmLite установлен через EXE. Используйте штатное удаление: Параметры Windows → Приложения → PdmLite.'
    }
    $paths = @((Join-Path $AppRoot 'AddIn'), (Join-Path $AppRoot 'Explorer'), $Manifest) + $Shortcuts
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            if (Test-Path -LiteralPath $path) { throw 'Файл или папка остались после удаления.' }
        } catch {
            throw "Удаление PdmLite завершено не полностью. Не удалось удалить: $path. $($_.Exception.Message)"
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $manifest = Join-Path $env:APPDATA "Autodesk\Inventor $InventorVersion\Addins\PdmLite.addin"
        $target = Join-Path $env:LOCALAPPDATA 'PdmLite'
        $shortcuts = @(
            (Join-Path ([Environment]::GetFolderPath('Desktop')) 'PdmLite Explorer.lnk'),
            (Join-Path ([Environment]::GetFolderPath('Programs')) 'PdmLite')
        )
        Uninstall-PdmLite $target $manifest $shortcuts
        Write-Host 'PdmLite удалён. Библиотека, база и настройки сохранены.' -ForegroundColor Green
    } catch {
        Write-Error $_ -ErrorAction Continue
        exit 1
    }
}
