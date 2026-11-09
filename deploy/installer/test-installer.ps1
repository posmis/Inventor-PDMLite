# Static PowerShell validation plus real compilation of both Inno recipes.
# Stub payloads exercise packaging only; they are never published as release assets.
param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
foreach ($file in @((Join-Path $repo 'deploy\build-release.ps1')) + @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' | Select-Object -ExpandProperty FullName)) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw ($errors | Out-String) }
}
. (Join-Path $PSScriptRoot 'find-inno-setup.ps1')
$IsccPath = Find-InnoSetupCompiler -ExplicitPath $IsccPath
if (-not $IsccPath) { throw 'Inno Setup 6.3+ is required.' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pdmlite-installer-check-' + [guid]::NewGuid().ToString('N'))
try {
    $payload = Join-Path $temp 'payload'
    foreach ($file in @('Explorer\PdmLite.Explorer.exe','AddIn\PdmLite.AddIn.dll','Setup\PdmLite.Setup.exe','Setup\library.zip','Setup\postgresql.exe')) {
        $path = Join-Path $payload $file
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        [IO.File]::WriteAllText($path, 'Packaging test only')
    }
    foreach ($variant in @('Online','Offline')) {
        & $IsccPath "/DPayloadDir=$payload" "/DOutputDir=$temp" "/DVariant=$variant" (Join-Path $PSScriptRoot 'PdmLite.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno compilation failed: $variant" }
    }
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
