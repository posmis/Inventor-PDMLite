function New-PdmPrivateFolder {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-pg-secret-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path | Out-Null
    try {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        foreach ($sid in @($owner, 'S-1-5-32-544', 'S-1-5-18') | Select-Object -Unique) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        }
        Set-Acl -LiteralPath $path -AclObject $acl
        return $path
    } catch {
        Remove-Item -LiteralPath $path -Recurse -Force
        throw
    }
}

function Install-PdmPostgresPackage([string]$Secret, [ValidateRange(1,65535)][int]$Port) {
    if ([string]::IsNullOrWhiteSpace($Secret) -or $Secret.IndexOfAny([char[]]@("`r", "`n", [char]0)) -ge 0) {
        throw 'Пароль установки не должен содержать переводы строк или NUL.'
    }
    $private = New-PdmPrivateFolder
    try {
        $options = Join-Path $private 'postgresql.options'
        # ACL установлен до записи. В аргументах процессов остаётся только путь к файлу.
        [IO.File]::WriteAllText($options, ('superpassword=' + $Secret), [Text.UTF8Encoding]::new($false))
        $override = '--optionfile "{0}" --mode unattended --unattendedmodeui minimal --serverport {1} --disable-components stackbuilder' -f $options, $Port
        & winget install --id PostgreSQL.PostgreSQL.16 --exact --silent --accept-source-agreements --accept-package-agreements --override $override
        if ($LASTEXITCODE -ne 0) { throw "Установка PostgreSQL не удалась (код $LASTEXITCODE)." }
    } finally { Remove-Item -LiteralPath $private -Recurse -Force }
}

function Start-PdmPsqlProcess([Diagnostics.ProcessStartInfo]$Info) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $Info
    try { [void]$process.Start(); return $process }
    catch { $process.Dispose(); throw }
}

function Invoke-PdmSql([string]$Psql, [ValidateRange(1,65535)][int]$Port, [string]$Secret, [string]$Sql) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Psql
    $info.Arguments = '-X -w -q -h localhost -p {0} -U postgres -d postgres -v ON_ERROR_STOP=1 -tA -f -' -f $Port
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    # Не изменяет PGPASSWORD родительского PowerShell даже при исключении.
    $info.EnvironmentVariables['PGPASSWORD'] = $Secret
    $info.EnvironmentVariables['PGCLIENTENCODING'] = 'UTF8'
    $info.EnvironmentVariables['PGCONNECT_TIMEOUT'] = '10'
    $process = Start-PdmPsqlProcess $info
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $writer = [IO.StreamWriter]::new($process.StandardInput.BaseStream, [Text.UTF8Encoding]::new($false))
        try { $writer.WriteLine("SET standard_conforming_strings = on;`n" + $Sql) } finally { $writer.Dispose() }
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Превышено время ожидания PostgreSQL.' }
        $result = $stdout.GetAwaiter().GetResult()
        [void]$stderr.GetAwaiter().GetResult()
        # psql может включать SQL (и пароль роли) в диагностический текст: не выводим его.
        if ($process.ExitCode -ne 0) { throw "Ошибка PostgreSQL (код $($process.ExitCode)). Проверьте пароль, права доступа и порт сервера." }
        return $result.Trim()
    } finally { $process.Dispose() }
}

function ConvertTo-PdmSqlIdentifier([string]$Value) { return '"' + $Value.Replace('"','""') + '"' }
function ConvertTo-PdmSqlLiteral([string]$Value) { return "'" + $Value.Replace("'","''") + "'" }

function New-PdmDatabasePassword {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes); return [BitConverter]::ToString($bytes).Replace('-','') }
    finally { $rng.Dispose(); [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Save-PdmDatabaseSettings([string]$Database, [string]$User, [int]$Port, [string]$Password,
    [string]$Folder = (Join-Path $env:APPDATA 'PdmLite')) {
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    $secure = ConvertTo-SecureString $Password -AsPlainText -Force
    try {
        $settings = [ordered]@{ Host='localhost'; Port=$Port; Database=$Database; Username=$User; ProtectedPassword=(ConvertFrom-SecureString $secure) }
        [IO.File]::WriteAllText((Join-Path $Folder 'database.json'), ($settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    } finally { $secure.Dispose() }
}
