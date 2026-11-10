function Write-ReleaseHashes {
    param(
        [Parameter(Mandatory=$true)][string]$OutputDirectory,
        [Parameter(Mandatory=$true)][ValidateSet('Online','Offline')][string[]]$Variants
    )
    # Сначала проверяются все ожидаемые результаты. Маска каталога не определяет состав релиза.
    $selected = @($Variants | Select-Object -Unique)
    $hashes = @(foreach ($variant in $selected) {
        $path = Join-Path $OutputDirectory ('PdmLite-Setup-0.1-{0}.exe' -f $variant)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Не найден результат сборки: $path" }
        Get-FileHash -LiteralPath $path -Algorithm SHA256
    })
    for ($i = 0; $i -lt $selected.Count; $i++) {
        $line = '{0}  {1}' -f $hashes[$i].Hash, [IO.Path]::GetFileName($hashes[$i].Path)
        $line | Set-Content -LiteralPath (Join-Path $OutputDirectory ('SHA256SUMS-{0}.txt' -f $selected[$i])) -Encoding ASCII
    }
    $hashes | ForEach-Object { '{0}  {1}' -f $_.Hash, [IO.Path]::GetFileName($_.Path) } |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
    return $hashes
}
