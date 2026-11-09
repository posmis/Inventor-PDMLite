# Only this step runs elevated; application settings belong to the original user.
param(
    [string]$Installer,
    [string]$Sha256,
    [string]$PipeName
)
$ErrorActionPreference = 'Stop'

function Read-PostgresSecret([string]$Name) {
    if ($Name -notmatch '^pdmlite-setup-[a-f0-9]{32}$') { throw 'Invalid setup channel.' }
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $Name, [IO.Pipes.PipeDirection]::In)
    try {
        $pipe.Connect(120000)
        $reader = [IO.StreamReader]::new($pipe)
        try { $value = $reader.ReadLine() } finally { $reader.Dispose() }
        if ($value -notmatch '^[A-F0-9]{64}$') { throw 'Invalid setup credential.' }
        return $value
    } finally { $pipe.Dispose() }
}

function Invoke-PostgresPackage([string]$Exe, [string]$Hash, [string]$Secret, [string]$Prefix, [string]$Data) {
    if ((Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash -ne $Hash) { throw 'PostgreSQL installer checksum mismatch.' }
    if ($Secret -notmatch '^[A-F0-9]{64}$') { throw 'Invalid setup credential.' }
    $private = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-pg-options-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $private | Out-Null
        # Restrict access BEFORE writing the secret. Works with UAC under another administrator.
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @('S-1-5-32-544','S-1-5-18')) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $private -AclObject $acl
        $optionFile = Join-Path $private 'postgresql.options'
        [IO.File]::WriteAllText($optionFile, ('superpassword=' + $Secret), [Text.Encoding]::ASCII)
        $arguments = '--optionfile "{0}" --mode unattended --unattendedmodeui none --prefix "{1}" --datadir "{2}" --servicename pdmlite-postgresql-16 --serverport 55439 --enable-components server,commandlinetools --disable-components pgAdmin,stackbuilder' -f $optionFile, $Prefix, $Data
        $process = Start-Process -FilePath $Exe -ArgumentList $arguments -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "PostgreSQL installer failed: $($process.ExitCode)." }
    } finally {
        if (Test-Path -LiteralPath $private) { Remove-Item -LiteralPath $private -Recurse -Force }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
try {
    if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $Sha256) {
        throw 'PostgreSQL installer checksum mismatch.'
    }
    $password = Read-PostgresSecret $PipeName
    $serviceName = 'pdmlite-postgresql-16'
    $prefix = Join-Path $env:ProgramFiles 'PdmLite\PostgreSQL16'
    $data = Join-Path $env:ProgramData 'PdmLite\PostgreSQL16\data'
    if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
        if (Test-Path -LiteralPath $data) { throw "Existing PostgreSQL data found at $data. Automatic overwrite is forbidden." }
        if (Get-NetTCPConnection -LocalPort 55439 -State Listen -ErrorAction SilentlyContinue) {
            throw 'Port 55439 is already in use. No existing server was changed.'
        }
        # EDB's major-version installer may reuse an existing product registration.
        # Never let it upgrade somebody else's PostgreSQL 16 instance implicitly.
        $registered = Get-ChildItem 'HKLM:\SOFTWARE\PostgreSQL\Installations' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ItemProperty $_.PSPath }
        foreach ($instance in $registered) {
            $version = $instance.Version
            if ($version -like '16.*' -or $instance.PSChildName -like '*-16') {
                throw 'Another PostgreSQL 16 installation already exists. Configure its connection in PdmLite before installing; it will not be overwritten.'
            }
        }
        if (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'PostgreSQL\16\bin\postgres.exe')) {
            throw 'PostgreSQL 16 already exists. Configure its connection in PdmLite; automatic replacement is forbidden.'
        }
        Invoke-PostgresPackage $Installer $Sha256 $password $prefix $data
    }
    # A dedicated local service; unrelated PostgreSQL installations are untouched.
    Add-Content -LiteralPath (Join-Path $data 'postgresql.conf') -Encoding ASCII -Value "`r`n# PdmLite local installation`r`nlisten_addresses = 'localhost'`r`nport = 55439"
    Restart-Service -Name $serviceName
    exit 0
} catch {
    Write-Error $_
    exit 1
}

}
