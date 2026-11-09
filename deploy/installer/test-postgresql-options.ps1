# Execute under Windows PowerShell in CI: validate actual NTFS ACLs and secret cleanup.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-postgresql-machine.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-options-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$exe = Join-Path $temp 'installer.exe'
[IO.File]::WriteAllText($exe, 'test installer')
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$script:Secret = 'A' * 64
$script:OptionFile = $null
$script:Fail = $false
$script:Calls = 0
function Start-Process {
    param($FilePath,$ArgumentList,[switch]$Wait,[switch]$PassThru)
    $script:Calls++
    if ($ArgumentList.Contains($script:Secret) -or $ArgumentList.Contains('--superpassword')) { throw 'Secret present in child command line' }
    if ($ArgumentList -notmatch '--optionfile "([^"]+)"') { throw 'Missing option file' }
    $script:OptionFile = $Matches[1]
    if ([IO.File]::ReadAllText($script:OptionFile) -ne ('superpassword=' + $script:Secret)) { throw 'Secret not passed to package' }
    $acl = Get-Acl -LiteralPath (Split-Path -Parent $script:OptionFile)
    if (-not $acl.AreAccessRulesProtected) { throw 'Secret directory inherits access rules' }
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.IdentityReference.Value -notin @('S-1-5-32-544','S-1-5-18')) { throw 'Secret readable by unexpected identity' }
    }
    if ($script:Fail) { return [pscustomobject]@{ ExitCode=1 } }
    return [pscustomobject]@{ ExitCode=0 }
}
try {
    Invoke-PostgresPackage $exe $hash $script:Secret 'C:\Program Files\PdmLite' 'C:\ProgramData\PdmLite'
    if (Test-Path -LiteralPath $script:OptionFile) { throw 'Secret survived successful installation' }
    $script:Fail = $true; $failed = $false
    try { Invoke-PostgresPackage $exe $hash $script:Secret 'C:\Program Files\PdmLite' 'C:\ProgramData\PdmLite' } catch { $failed = $true }
    if (-not $failed) { throw 'Package failure was hidden' }
    if (Test-Path -LiteralPath $script:OptionFile) { throw 'Secret survived failed installation' }
    if ($script:Calls -ne 2) { throw 'Both package success and failure were not exercised' }
    $failed = $false
    try { Invoke-PostgresPackage $exe ('0' * 64) $script:Secret 'C:\Program Files\PdmLite' 'C:\ProgramData\PdmLite' } catch { $failed = $true }
    if (-not $failed -or $script:Calls -ne 2) { throw 'Unverified package was launched' }
    $failed = $false
    try { Invoke-PostgresPackage $exe $hash 'invalid' 'C:\Program Files\PdmLite' 'C:\ProgramData\PdmLite' } catch { $failed = $true }
    if (-not $failed -or $script:Calls -ne 2) { throw 'Invalid credential was passed to package' }
    Write-Output '4 PostgreSQL option-file scenarios passed (command line, NTFS ACL, cleanup and input verification).'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
