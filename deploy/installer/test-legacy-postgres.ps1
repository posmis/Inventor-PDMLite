$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'deploy/install-postgres.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-legacy-tests-' + [guid]::NewGuid().ToString('N'))
$windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$savedEnvironment = @{}
foreach ($name in @('PGPASSWORD','PDMLITE_FAKE_PSQL_CAPTURE','PDMLITE_FAKE_PSQL_EXIT','PDMLITE_FAKE_ROLE_EXISTS')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $fixture = Join-Path $repo 'tests/Fixtures/PdmLite.FakePsql/PdmLite.FakePsql.csproj'
    $output = Join-Path $temp 'psql'
    & dotnet build $fixture -c Release -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fake psql build failed' }
    $script:Psql = Join-Path $output $(if ($windows) { 'PdmLite.FakePsql.exe' } else { 'PdmLite.FakePsql' })
    $env:PDMLITE_FAKE_PSQL_CAPTURE = Join-Path $temp 'capture'
    $env:PDMLITE_FAKE_PSQL_EXIT = '0'
    $env:PDMLITE_FAKE_ROLE_EXISTS = '0'
    $env:PGPASSWORD = 'parent-environment-sentinel'
    # Только синтетические значения: фиктивный psql сохраняет входные данные для проверок.
    $script:Super = 'Synthetic admin "quote"  Секрет'
    $script:OptionFile = $null
    $script:WingetExit = 0
    $script:WingetCalls = 0
    if (-not $windows) {
        # NTFS ACL API недоступен в Linux; Windows CI использует настоящую New-PdmPrivateFolder.
        function New-PdmPrivateFolder {
            $path = Join-Path $temp ([guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $path | Out-Null
            return $path
        }
    }
    function winget {
        $script:WingetCalls++
        $text = $args -join ' '
        if ($text.Contains($script:Super) -or $text.Contains('--superpassword')) { throw 'Secret present in winget arguments' }
        if ($text -notmatch '--optionfile "([^"]+)"') { throw 'Missing option file' }
        $script:OptionFile = $Matches[1]
        if ([IO.File]::ReadAllText($script:OptionFile) -ne ('superpassword=' + $script:Super)) { throw 'Wrong option-file content' }
        if ($windows) {
            $acl = Get-Acl (Split-Path -Parent $script:OptionFile)
            if (-not $acl.AreAccessRulesProtected) { throw 'Secret directory inherits permissions' }
            $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-32-544', 'S-1-5-18')
            foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
                if ($rule.IdentityReference.Value -notin $allowed) { throw 'Unexpected reader of the secret directory' }
            }
        }
        $global:LASTEXITCODE = $script:WingetExit
    }
    Install-PdmPostgresPackage $script:Super 5432
    if (Test-Path $script:OptionFile) { throw 'Secret survived successful installation' }
    $script:WingetExit = 7; $failed = $false
    try { Install-PdmPostgresPackage $script:Super 5432 } catch { $failed = $true }
    if (-not $failed -or (Test-Path $script:OptionFile)) { throw 'Failed installation did not clean the secret' }
    $failed = $false
    try { Install-PdmPostgresPackage ($script:Super + "`nextra=value") 5432 } catch { $failed = $true }
    if (-not $failed -or $script:WingetCalls -ne 2) { throw 'Invalid option-file value reached winget' }

    $sql = 'CREATE ROLE "role" PASSWORD ''synthetic-role-password'';'
    $null = Invoke-PdmSql $script:Psql 5432 $script:Super $sql
    $record = Get-ChildItem $env:PDMLITE_FAKE_PSQL_CAPTURE -Filter '*.json' | Select-Object -First 1 | Get-Content -Raw | ConvertFrom-Json
    $arguments = $record.arguments -join ' '
    if ($arguments.Contains($script:Super) -or $arguments.Contains('synthetic-role-password') -or $arguments -match '(^| )-c( |$)') { throw 'Secret SQL present in psql command line' }
    if ($record.password -ne $script:Super -or -not $record.sql.Contains($sql)) { throw 'Child did not receive password/environment and SQL/stdin' }
    if ($env:PGPASSWORD -ne 'parent-environment-sentinel') { throw 'Parent environment changed' }
    $env:PDMLITE_FAKE_PSQL_EXIT = '9'; $failed = $false
    try { $null = Invoke-PdmSql $script:Psql 5432 $script:Super $sql }
    catch {
        $failed = $true
        if ($_.Exception.Message.Contains($script:Super) -or $_.Exception.Message.Contains('synthetic-role-password')) { throw 'SQL or credential leaked in failure output' }
    }
    if (-not $failed -or $env:PGPASSWORD -ne 'parent-environment-sentinel') { throw 'psql error was hidden or environment was changed' }

    $saveSettings = ${function:Save-PdmDatabaseSettings}
    function Find-Psql { return $script:Psql }
    function Read-Host { param($Prompt,[switch]$AsSecureString) $script:ReadSecret = ConvertTo-SecureString $script:Super -AsPlainText -Force; return $script:ReadSecret }
    function Save-PdmDatabaseSettings { param($Database,$User,$Port,$Password) $script:SavedPassword = $Password }
    $env:PDMLITE_FAKE_PSQL_EXIT = '0'; $script:SavedPassword = $null
    Initialize-PdmPostgres 'database"quoted' "role'quoted" 5432
    if ($script:SavedPassword -notmatch '^[A-F0-9]{64}$') { throw 'Missing generated database password' }
    $all = @(Get-ChildItem $env:PDMLITE_FAKE_PSQL_CAPTURE -Filter '*.json' | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
    if (-not ($all | Where-Object { $_.sql.Contains('CREATE DATABASE "database""quoted"') })) { throw 'Database identifier not escaped' }
    if (-not ($all | Where-Object { $_.sql.Contains("rolname = 'role''quoted'") })) { throw 'Role literal not escaped' }
    if (-not ($all | Where-Object { $_.sql.Contains($script:SavedPassword) })) { throw 'Generated role password not sent over stdin' }
    foreach ($call in $all) {
        if (($call.arguments -join ' ').Contains($script:SavedPassword)) { throw 'Generated role password leaked to arguments' }
    }
    $disposed = $false
    try { $null = $script:ReadSecret.Copy() } catch { $disposed = $true }
    if (-not $disposed) { throw 'SecureString was not disposed' }
    $env:PDMLITE_FAKE_ROLE_EXISTS = '1'; $script:SavedPassword = $null; $failed = $false
    try { Initialize-PdmPostgres 'database' 'existing-role' 5432 } catch { $failed = $true }
    if (-not $failed -or $script:SavedPassword -or $env:PGPASSWORD -ne 'parent-environment-sentinel') { throw 'Existing role was overwritten or parent password survived' }
    Set-Item Function:Save-PdmDatabaseSettings -Value $saveSettings
    if ($windows) {
        $settings = Join-Path $temp 'settings'
        Save-PdmDatabaseSettings 'database' 'role' 5432 'synthetic-pdm-password' $settings
        $json = Get-Content (Join-Path $settings 'database.json') -Raw | ConvertFrom-Json
        if ($json.ProtectedPassword -eq 'synthetic-pdm-password') { throw 'Unencrypted settings password' }
        $secure = ConvertTo-SecureString $json.ProtectedPassword
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            if ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) -ne 'synthetic-pdm-password') { throw 'DPAPI roundtrip failed' }
        } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr); $secure.Dispose() }
    }
    Write-Output 'Legacy PostgreSQL checks passed: option file, cleanup, real child argv/stdin/environment, error redaction, existing role and settings.'
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    Remove-Item -LiteralPath $temp -Recurse -Force
}
