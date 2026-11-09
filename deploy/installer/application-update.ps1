# Program folders are replaced as units: obsolete DLLs cannot survive an upgrade.
param([ValidateSet('Begin','Rollback','Commit')][string]$Action, [string]$App, [string]$Transaction)
$ErrorActionPreference = 'Stop'

function Get-UpdatePaths([string]$App, [string]$Transaction) {
    $root = [IO.Path]::GetFullPath($App).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $work = [IO.Path]::GetFullPath($Transaction)
    if ([IO.Path]::GetDirectoryName($work) -ne $root -or [IO.Path]::GetFileName($work) -notmatch '^\.update-[a-f0-9]{32}$') {
        throw 'Invalid application update folder.'
    }
    foreach ($path in @($root, $work)) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Application update cannot follow a directory link.'
        }
    }
    return @{ Root = $root; Work = $work; Journal = (Join-Path $work 'update.json') }
}

function Write-UpdateJournal($Paths, $Journal) {
    $temporary = $Paths.Journal + '.tmp'
    $Journal | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary -Encoding UTF8
    if (Test-Path -LiteralPath $Paths.Journal) { [IO.File]::Replace($temporary, $Paths.Journal, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $Paths.Journal) }
}

function Remove-ProgramTree([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer) {
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            foreach ($child in (Get-ChildItem -LiteralPath $Path -Force)) { Remove-ProgramTree $child.FullName }
        }
        [IO.Directory]::Delete($Path)
    } else {
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            [IO.File]::SetAttributes($Path, ($item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)))
        }
        [IO.File]::Delete($Path)
    }
}

function Assert-ProgramFoldersContainNoData([string]$App, [string[]]$DataFolders) {
    foreach ($folder in @('Explorer','AddIn','Setup')) {
        $program = [IO.Path]::GetFullPath((Join-Path $App $folder)).TrimEnd([IO.Path]::DirectorySeparatorChar)
        foreach ($data in $DataFolders) {
            if (-not $data) { continue }
            $full = [IO.Path]::GetFullPath($data)
            if ($full.Equals($program, [StringComparison]::OrdinalIgnoreCase) -or
                $full.StartsWith($program + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Library or backups are stored inside a program folder. Move them through PdmLite settings before updating.'
            }
        }
    }
}

function Start-ApplicationUpdate([string]$App, [string]$Transaction) {
    $paths = Get-UpdatePaths $App $Transaction
    if (Test-Path -LiteralPath $paths.Work) { throw 'Update transaction already exists.' }
    $librarySettings = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'PdmLite/library.json'
    if (Test-Path -LiteralPath $librarySettings) {
        $library = Get-Content -LiteralPath $librarySettings -Raw | ConvertFrom-Json
        Assert-ProgramFoldersContainNoData $paths.Root @($library.RootFolder, $library.BackupFolder)
    }
    $folders = @('Explorer','AddIn','Setup') | ForEach-Object {
        $path = Join-Path $paths.Root $_
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Program folder is a directory link; automatic update refused.'
        }
        @{ Name = $_; Existed = (Test-Path -LiteralPath $path) }
    }
    New-Item -ItemType Directory -Path $paths.Work -Force | Out-Null
    $journal = @{ Phase = 'Staging'; Folders = @($folders) }
    Write-UpdateJournal $paths $journal
    foreach ($folder in $folders) {
        if ($folder.Existed) {
            Move-Item -LiteralPath (Join-Path $paths.Root $folder.Name) -Destination (Join-Path $paths.Work $folder.Name)
        }
    }
    $journal.Phase = 'Staged'
    Write-UpdateJournal $paths $journal
}

function Undo-ApplicationUpdate([string]$App, [string]$Transaction) {
    $paths = Get-UpdatePaths $App $Transaction
    if (-not (Test-Path -LiteralPath $paths.Journal)) {
        if ((Test-Path -LiteralPath $paths.Work) -and (Get-ChildItem -LiteralPath $paths.Work -Force)) { throw 'Update journal missing; backup files retained.' }
        return
    }
    $journal = Get-Content -LiteralPath $paths.Journal -Raw | ConvertFrom-Json
    if ($journal.Phase -eq 'Committed') { return }
    if ($journal.Phase -notin @('Staged','Staging')) { throw 'Invalid update state; backup files retained.' }
    foreach ($folder in $journal.Folders) {
        if ($folder.Name -notin @('Explorer','AddIn','Setup')) { throw 'Invalid update journal.' }
        $old = Join-Path $paths.Work $folder.Name
        $current = Join-Path $paths.Root $folder.Name
        if (Test-Path -LiteralPath $old) {
            if (Test-Path -LiteralPath $current) { Remove-ProgramTree $current }
            Move-Item -LiteralPath $old -Destination $current
        } elseif (-not $folder.Existed -and $journal.Phase -eq 'Staged') {
            if (Test-Path -LiteralPath $current) { Remove-ProgramTree $current }
        }
    }
    Remove-ProgramTree $paths.Work
}

function Complete-ApplicationUpdate([string]$App, [string]$Transaction) {
    $paths = Get-UpdatePaths $App $Transaction
    $journal = Get-Content -LiteralPath $paths.Journal -Raw | ConvertFrom-Json
    if ($journal.Phase -notin @('Staged','Committed')) { throw 'Cannot commit an incomplete update.' }
    $journal.Phase = 'Committed'
    Write-UpdateJournal $paths $journal
    # Cleanup failure must not roll back an already successful installation.
    try { Remove-ProgramTree $paths.Work } catch { Write-Warning 'Old application files retained in the update backup folder.' }
}

if ($Action) {
    try {
        switch ($Action) {
            Begin { Start-ApplicationUpdate $App $Transaction }
            Rollback { Undo-ApplicationUpdate $App $Transaction }
            Commit { Complete-ApplicationUpdate $App $Transaction }
        }
    } catch { Write-Error $_; exit 1 }
}
