$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -CommandType Application).Source
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-process-tests-' + [guid]::NewGuid().ToString('N'))
$oldPath = $env:PATH
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $target = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'Inventor.targets'))
    $project = Join-Path $temp 'probe.proj'
    [IO.File]::WriteAllText($project, ('<Project><Import Project="{0}" /></Project>' -f $target))
    # Имитирует затенение команд Git/Cygwin в PATH; новая проверка не должна запускать их.
    foreach ($name in @('find','tasklist')) {
        [IO.File]::WriteAllText((Join-Path $temp "$name.cmd"), ('@echo called>{0}' -f (Join-Path $temp 'unexpected-tool.txt')))
    }
    $env:PATH = $temp + [IO.Path]::PathSeparator + $oldPath
    function Check([string]$Name, [bool]$Blocked, [bool]$Disabled = $false) {
        $arguments = @('msbuild',$project,'-nologo','-t:CheckInventorNotRunning','-p:OS=Windows_NT',"-p:PdmLiteInventorProcessName=$Name")
        if ($Disabled) { $arguments += '-p:PdmLiteCheckInventorNotRunning=false' }
        $result = & $dotnet @arguments 2>&1
        if (($LASTEXITCODE -ne 0) -ne $Blocked) { throw "Process check returned wrong status: $result" }
    }
    Check ('MissingPdmLite' + [guid]::NewGuid().ToString('N')) $false
    Check 'dotnet' $true
    Check 'dotnet' $false $true
    if (Test-Path (Join-Path $temp 'unexpected-tool.txt')) { throw 'Process check invoked a PATH-dependent tool' }
    Write-Output '3 MSBuild process-check scenarios passed with a shadowed PATH.'
} finally { $env:PATH = $oldPath; Remove-Item -LiteralPath $temp -Recurse -Force }
