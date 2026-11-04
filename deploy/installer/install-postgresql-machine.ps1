# Only this step runs elevated; application settings belong to the original user.
param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$Sha256,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-F0-9]{64}$')][string]$Password
)
$ErrorActionPreference = 'Stop'
try {
    if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $Sha256) {
        throw 'PostgreSQL installer checksum mismatch.'
    }
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
        $arguments = '--mode unattended --unattendedmodeui none --prefix "{0}" --datadir "{1}" --servicename {2} --serverport 55439 --superpassword {3} --enable-components server,commandlinetools --disable-components pgAdmin,stackbuilder' -f $prefix, $data, $serviceName, $Password
        $process = Start-Process -FilePath $Installer -ArgumentList $arguments -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "PostgreSQL installer failed: $($process.ExitCode)." }
    }
    # A dedicated local service; unrelated PostgreSQL installations are untouched.
    Add-Content -LiteralPath (Join-Path $data 'postgresql.conf') -Encoding ASCII -Value "`r`n# PdmLite local installation`r`nlisten_addresses = 'localhost'`r`nport = 55439"
    Restart-Service -Name $serviceName
    exit 0
} catch {
    Write-Error $_
    exit 1
}
