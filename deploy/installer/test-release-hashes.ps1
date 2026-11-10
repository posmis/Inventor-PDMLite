$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-hashes.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-release-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    foreach ($name in @('Online','Offline','Old-Dated-Build')) {
        [IO.File]::WriteAllText((Join-Path $temp "PdmLite-Setup-0.1-$name.exe"), $name)
    }
    foreach ($variant in @('Online','Offline')) {
        $hashes = @(Write-ReleaseHashes $temp @($variant))
        $lines = @(Get-Content (Join-Path $temp 'SHA256SUMS.txt'))
        if ($lines.Count -ne 1 -or $lines[0] -notmatch "PdmLite-Setup-0.1-$variant.exe$" -or $hashes.Count -ne 1) { throw 'Single build includes stale installer' }
        if ($lines[0] -ne ((Get-FileHash $hashes[0].Path).Hash + '  ' + [IO.Path]::GetFileName($hashes[0].Path))) { throw 'Wrong checksum' }
    }
    $null = Write-ReleaseHashes $temp @('Online','Offline')
    if (@(Get-Content (Join-Path $temp 'SHA256SUMS.txt')).Count -ne 2) { throw 'Both build has wrong artifact list' }
    foreach ($variant in @('Online','Offline')) {
        if (@(Get-Content (Join-Path $temp "SHA256SUMS-$variant.txt")).Count -ne 1) { throw 'Missing per-variant manifest' }
    }
    $before = [IO.File]::ReadAllText((Join-Path $temp 'SHA256SUMS.txt'))
    Remove-Item (Join-Path $temp 'PdmLite-Setup-0.1-Offline.exe')
    $failed = $false
    try { $null = Write-ReleaseHashes $temp @('Online','Offline') } catch { $failed = $true }
    if (-not $failed -or [IO.File]::ReadAllText((Join-Path $temp 'SHA256SUMS.txt')) -ne $before) { throw 'Missing artifact was silently accepted' }
    Write-Output '4 release hash scenarios passed.'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
