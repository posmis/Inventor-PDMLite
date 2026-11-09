$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../uninstall.ps1')
function Assert-Uninstall($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$script:Running = $null
function Get-Process {
    param($Name,$ErrorAction)
    if ($script:Running -and @($Name) -contains $script:Running) { return [pscustomobject]@{ ProcessName=$script:Running } }
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-uninstall-tests-' + [guid]::NewGuid().ToString('N'))
function New-UninstallCase([string]$Name) {
    $root = Join-Path $temp $Name
    $app = Join-Path $root 'App'
    foreach ($folder in @('AddIn','Explorer')) {
        New-Item -ItemType Directory -Path (Join-Path $app $folder) -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $app "$folder/program.dll"), 'program')
    }
    $manifest = Join-Path $root 'PdmLite.addin'; [IO.File]::WriteAllText($manifest, 'manifest')
    $shortcut = Join-Path $root 'PdmLite.lnk'; [IO.File]::WriteAllText($shortcut, 'shortcut')
    $data = Join-Path $root 'library.ipt'; [IO.File]::WriteAllText($data, 'user data')
    return @{ App=$app; Manifest=$manifest; Shortcut=$shortcut; Data=$data }
}
$count=0
try {
    foreach ($hostName in @('Inventor','PdmLite.Explorer')) {
        $case = New-UninstallCase $hostName; $script:Running=$hostName
        $failed=$false
        try { Uninstall-PdmLite $case.App $case.Manifest @($case.Shortcut) } catch { $failed=$true }
        Assert-Uninstall $failed 'Running host was ignored'
        Assert-Uninstall (Test-Path $case.Manifest) 'Registration removed despite running host'
        Assert-Uninstall (Test-Path (Join-Path $case.App 'Explorer/program.dll')) 'Program removed despite running host'
        Assert-Uninstall (Test-Path $case.Shortcut) 'Shortcut removed despite running host'
        $count++
    }
    $script:Running=$null
    foreach ($mode in @('Denied','NoOp')) {
        $case = New-UninstallCase $mode
        $script:BlockedPath=Join-Path $case.App 'Explorer'; $script:DeleteMode=$mode
        function Remove-Item {
            param($LiteralPath,[switch]$Recurse,[switch]$Force,$ErrorAction)
            if ($LiteralPath -eq $script:BlockedPath) {
                if ($script:DeleteMode -eq 'Denied') { throw [UnauthorizedAccessException]::new('Denied') }
                return
            }
            Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Recurse:$Recurse -Force:$Force -ErrorAction Stop
        }
        try {
            $failure=$null
            try { Uninstall-PdmLite $case.App $case.Manifest @($case.Shortcut) } catch { $failure=$_ }
            Assert-Uninstall ($null -ne $failure) 'Incomplete uninstall reported success'
            Assert-Uninstall ($failure.Exception.Message.Contains($script:BlockedPath)) 'Failure did not identify remaining files'
            Assert-Uninstall (Test-Path $case.Manifest) 'Registration removed before program deletion succeeded'
            Assert-Uninstall ([IO.File]::ReadAllText($case.Data) -eq 'user data') 'User data changed'
            $count++
        } finally { Microsoft.PowerShell.Management\Remove-Item Function:\Remove-Item }
    }
    $case=New-UninstallCase 'Success'
    Uninstall-PdmLite $case.App $case.Manifest @($case.Shortcut)
    Assert-Uninstall (-not (Test-Path (Join-Path $case.App 'Explorer'))) 'Explorer not removed'
    Assert-Uninstall (-not (Test-Path (Join-Path $case.App 'AddIn'))) 'AddIn not removed'
    Assert-Uninstall (-not (Test-Path $case.Manifest)) 'Manifest not removed'
    Assert-Uninstall (-not (Test-Path $case.Shortcut)) 'Shortcut not removed'
    Assert-Uninstall ([IO.File]::ReadAllText($case.Data) -eq 'user data') 'User data changed'
    $count++
    Uninstall-PdmLite $case.App $case.Manifest @($case.Shortcut); $count++
    $case=New-UninstallCase 'Inno'
    [IO.File]::WriteAllText((Join-Path $case.App 'unins000.exe'), 'official uninstaller')
    $failed=$false
    try { Uninstall-PdmLite $case.App $case.Manifest @($case.Shortcut) } catch { $failed=$true }
    Assert-Uninstall $failed 'EXE installation was partially removed by legacy script'
    Assert-Uninstall (Test-Path $case.Manifest) 'Inno registration changed'; $count++
    Write-Output "$count uninstall scenarios passed."
} finally { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $temp -Recurse -Force }
