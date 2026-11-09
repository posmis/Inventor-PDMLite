# Portable filesystem regression tests; also run on Windows in CI.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'application-update.ps1')
function Assert-Update($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-update-tests-' + [guid]::NewGuid().ToString('N'))
$count = 0
try {
    foreach ($mode in @('Commit','Rollback','FirstInstallRollback','PartialStageRollback')) {
        $app = Join-Path $temp $mode
        New-Item -ItemType Directory -Path $app -Force | Out-Null
        $transaction = Join-Path $app ('.update-' + [guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText((Join-Path $app 'user.keep'), 'user data')
        if ($mode -ne 'FirstInstallRollback') {
            foreach ($name in @('Explorer','AddIn','Setup')) {
                $folder = Join-Path $app $name
                New-Item -ItemType Directory -Path $folder | Out-Null
                [IO.File]::WriteAllText((Join-Path $folder 'PdmLite.Modules.Obsolete.dll'), 'old version')
            }
        }
        if ($mode -eq 'PartialStageRollback') {
            New-Item -ItemType Directory -Path $transaction | Out-Null
            $paths = Get-UpdatePaths $app $transaction
            Write-UpdateJournal $paths @{ Phase='Staging'; Folders=@(@{Name='Explorer';Existed=$true},@{Name='AddIn';Existed=$true},@{Name='Setup';Existed=$true}) }
            Move-Item -LiteralPath (Join-Path $app 'Explorer') -Destination (Join-Path $transaction 'Explorer')
        } else {
            Start-ApplicationUpdate $app $transaction
            foreach ($name in @('Explorer','AddIn','Setup')) {
                $folder = Join-Path $app $name
                New-Item -ItemType Directory -Path $folder | Out-Null
                [IO.File]::WriteAllText((Join-Path $folder 'new.dll'), 'new version')
            }
        }
        if ($mode -eq 'Commit') {
            Complete-ApplicationUpdate $app $transaction
            Assert-Update (-not (Test-Path (Join-Path $app 'Explorer/PdmLite.Modules.Obsolete.dll'))) 'Obsolete module survived update'
            Assert-Update (Test-Path (Join-Path $app 'Explorer/new.dll')) 'New version missing after commit'
        } else {
            Undo-ApplicationUpdate $app $transaction
            foreach ($name in @('Explorer','AddIn','Setup')) {
                Assert-Update (-not (Test-Path (Join-Path $app "$name/new.dll"))) 'New file survived rollback'
                if ($mode -ne 'FirstInstallRollback') {
                    Assert-Update ([IO.File]::ReadAllText((Join-Path $app "$name/PdmLite.Modules.Obsolete.dll")) -eq 'old version') 'Original files not restored'
                } else { Assert-Update (-not (Test-Path (Join-Path $app $name))) 'Fresh install folder survived rollback' }
            }
        }
        Assert-Update ([IO.File]::ReadAllText((Join-Path $app 'user.keep')) -eq 'user data') 'User files were touched'
        Assert-Update (-not (Test-Path $transaction)) 'Transaction backup not cleaned'
        $count++
    }
    $app = Join-Path $temp 'Unsafe'; New-Item -ItemType Directory -Path $app | Out-Null
    $failed = $false
    try { Start-ApplicationUpdate $app (Join-Path $temp '.update-00000000000000000000000000000000') } catch { $failed = $true }
    Assert-Update $failed 'Out-of-root transaction was allowed'; $count++
    $transaction = Join-Path $app ('.update-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $transaction | Out-Null
    $failed = $false; try { Start-ApplicationUpdate $app $transaction } catch { $failed = $true }
    Assert-Update $failed 'Existing transaction was overwritten'; $count++
    $failed = $false
    try { Assert-ProgramFoldersContainNoData $app @((Join-Path $app 'Explorer/Library')) } catch { $failed = $true }
    Assert-Update $failed 'Library data inside a program folder was not protected'; $count++
    Assert-ProgramFoldersContainNoData $app @((Join-Path $temp 'UserLibrary'), (Join-Path $temp 'UserBackups'))
    $count++
    $outside = Join-Path $temp 'Outside'; New-Item -ItemType Directory -Path $outside | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'keep.ipt'), 'library data')
    $linked = Join-Path $temp 'ProgramWithLink'; New-Item -ItemType Directory -Path $linked | Out-Null
    $linkType = if ($env:OS -eq 'Windows_NT') { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path (Join-Path $linked 'link') -Target $outside | Out-Null
    Remove-ProgramTree $linked
    Assert-Update ([IO.File]::ReadAllText((Join-Path $outside 'keep.ipt')) -eq 'library data') 'Cleanup followed a directory link'; $count++
    # Failure during the second folder move must preserve untouched originals and restore the first.
    $app = Join-Path $temp 'StageFailure'; New-Item -ItemType Directory -Path $app | Out-Null
    $transaction = Join-Path $app ('.update-' + [guid]::NewGuid().ToString('N'))
    foreach ($name in @('Explorer','AddIn','Setup')) {
        New-Item -ItemType Directory -Path (Join-Path $app $name) | Out-Null
        [IO.File]::WriteAllText((Join-Path $app "$name/old.dll"), 'original')
    }
    $script:FailDestination = Join-Path $transaction 'AddIn'
    function Move-Item {
        param($LiteralPath,$Destination)
        if ($Destination -eq $script:FailDestination) { throw 'Simulated locked program folder' }
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
    }
    try {
        $failed = $false; try { Start-ApplicationUpdate $app $transaction } catch { $failed = $true }
        Assert-Update $failed 'Stage failure was hidden'
        Undo-ApplicationUpdate $app $transaction
        foreach ($name in @('Explorer','AddIn','Setup')) {
            Assert-Update ([IO.File]::ReadAllText((Join-Path $app "$name/old.dll")) -eq 'original') 'Stage failure lost original files'
        }
        $count++
    } finally { Remove-Item Function:\Move-Item }
    # A blocked rollback must keep recoverable backups; retry after unlocking must restore them.
    Start-ApplicationUpdate $app $transaction
    New-Item -ItemType Directory -Path (Join-Path $app 'Explorer') | Out-Null
    $originalRemove = (Get-Item Function:\Remove-ProgramTree).ScriptBlock
    function Remove-ProgramTree { param($Path) throw 'Simulated locked new file' }
    try {
        $failed = $false; try { Undo-ApplicationUpdate $app $transaction } catch { $failed = $true }
        Assert-Update $failed 'Incomplete rollback was hidden'
        Assert-Update (Test-Path (Join-Path $transaction 'Explorer/old.dll')) 'Rollback deleted recovery files'
        $count++
    } finally { Set-Item Function:\Remove-ProgramTree -Value $originalRemove }
    Undo-ApplicationUpdate $app $transaction
    Assert-Update (Test-Path (Join-Path $app 'Explorer/old.dll')) 'Rollback retry did not restore original'
    # Cleanup failure must never convert a committed upgrade into a rollback.
    Start-ApplicationUpdate $app $transaction
    New-Item -ItemType Directory -Path (Join-Path $app 'Explorer') | Out-Null
    [IO.File]::WriteAllText((Join-Path $app 'Explorer/new.dll'), 'new')
    function Remove-ProgramTree { param($Path) throw 'Simulated backup cleanup failure' }
    try {
        Complete-ApplicationUpdate $app $transaction
        Undo-ApplicationUpdate $app $transaction
        Assert-Update (Test-Path (Join-Path $app 'Explorer/new.dll')) 'Committed update was rolled back after cleanup failure'
        $count++
    } finally { Set-Item Function:\Remove-ProgramTree -Value $originalRemove }
    Write-Output "$count application update scenarios passed."
} finally { if (Test-Path $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
