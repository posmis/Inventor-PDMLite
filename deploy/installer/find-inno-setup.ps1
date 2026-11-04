# Shared compiler detection for the release build and Windows packaging check.
function Find-InnoSetupCompiler {
    param([string]$ExplicitPath)
    if ($ExplicitPath) {
        if (Test-Path -LiteralPath $ExplicitPath -PathType Leaf) { return (Resolve-Path -LiteralPath $ExplicitPath).Path }
        throw "Не найден указанный компилятор Inno Setup: $ExplicitPath"
    }
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    # Inno Setup can be installed for one user, in either Program Files, or to a custom path.
    $keys = @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($entry in (Get-ItemProperty -Path $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Inno Setup*' })) {
        $location = $entry.InstallLocation
        if (-not $location -and $entry.UninstallString -match '^"([^"]+)"') {
            $location = Split-Path -Parent $Matches[1]
        }
        if ($location) {
            $candidate = Join-Path $location.Trim('"') 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432)
    if ($env:LOCALAPPDATA) {
        $roots += Join-Path $env:LOCALAPPDATA 'Programs'
        $roots += $env:LOCALAPPDATA
    }
    foreach ($root in ($roots | Where-Object { $_ } | Select-Object -Unique)) {
        foreach ($directory in (Get-ChildItem -LiteralPath $root -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
            $candidate = Join-Path $directory.FullName 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    return $null
}
