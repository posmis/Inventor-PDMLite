<#
.SYNOPSIS
    Собирает в одну папку файлы Inventor по списку имён — вместе с зависимостями сборок.

.DESCRIPTION
    Отдельная утилита (не часть PdmLite и не плагин Inventor). Например, после переноса электроники
    в другую папку: в Inventor «Диагностика сборки → Ненайденные файлы → Копировать имена», затем
    этот скрипт находит файлы по именам в папке с компонентами и копирует их в одну папку.

    - Поиск по имени файла во всей папке компонентов (с подпапками, без OldVersions).
    - У найденных сборок (.iam), чертежей и производных деталей читаются зависимости через Apprentice
      (Inventor или бесплатный Inventor View) — на всех уровнях. Зависимости тоже ищутся по имени
      в папке компонентов (старые пути из сборок не используются).
    - Несколько файлов с одним именем — берётся самый новый, остальные перечисляются в отчёте.
    - Отчёт: в окне и в файле _отчёт.txt в папке результата.

.PARAMETER List
    Текстовый файл со списком: по одному имени на строку (можно с путём — берётся имя файла).

.PARAMETER FromClipboard
    Взять список из буфера обмена (после «Копировать имена»).

.PARAMETER Source
    Папка с компонентами, где искать.

.PARAMETER Target
    Папка, куда копировать (создаётся).

.PARAMETER NoDependencies
    Не собирать зависимости сборок — только файлы из списка.

.PARAMETER DryRun
    Только показать, что будет скопировано, ничего не копировать.

.EXAMPLE
    .\Collect-Components.ps1 -FromClipboard -Source "D:\Компоненты" -Target "D:\Собрано"

.EXAMPLE
    .\Collect-Components.ps1 -List .\список.txt -Source "D:\Компоненты" -Target "D:\Собрано" -DryRun
#>
[CmdletBinding()]
param(
    [string]$List,
    [switch]$FromClipboard,
    [string]$Source,
    [string]$Target,
    [switch]$NoDependencies,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$InventorExtensions = @('.ipt', '.iam', '.idw', '.dwg', '.ipn')

function Select-Folder([string]$title) {
    $shell = New-Object -ComObject Shell.Application
    $folder = $shell.BrowseForFolder(0, $title, 0x51, 0)
    if ($null -eq $folder) { return $null }
    return $folder.Self.Path
}

function Read-Names {
    $lines = @()
    if ($FromClipboard) {
        $lines = @(Get-Clipboard)
    } elseif ($List) {
        if (-not (Test-Path -LiteralPath $List)) { throw "Нет файла списка: $List" }
        $lines = @(Get-Content -LiteralPath $List -Encoding UTF8)
    } else {
        Write-Host 'Список не указан — беру из буфера обмена.' -ForegroundColor Yellow
        $lines = @(Get-Clipboard)
    }
    $names = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $lines) {
        if ($null -eq $line) { continue }
        $text = $line.Trim().Trim('"')
        if ($text.Length -eq 0) { continue }
        # Строка из таблицы (через табуляцию) — первое поле; с путём — только имя файла.
        $text = ($text -split "`t")[0].Trim()
        $name = [System.IO.Path]::GetFileName($text)
        if ($name.Length -gt 0 -and $seen.Add($name)) { $names.Add($name) }
    }
    return ,$names
}

# ---------- Параметры ----------

if (-not $Source) { $Source = Select-Folder 'Папка с компонентами (где искать)' }
if (-not $Source) { throw 'Не выбрана папка с компонентами.' }
if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Нет папки: $Source" }
if (-not $Target) { $Target = Select-Folder 'Папка, куда собрать файлы' }
if (-not $Target) { throw 'Не выбрана папка для результата.' }
$Source = (Resolve-Path -LiteralPath $Source).Path

$names = Read-Names
if ($names.Count -eq 0) { throw 'Список пуст.' }
Write-Host "В списке файлов: $($names.Count)"

# ---------- Индекс папки компонентов: имя → файлы ----------

Write-Host "Читаю папку $Source …"
$byName = @{}
Get-ChildItem -LiteralPath $Source -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/]OldVersions[\\/]' } |
    ForEach-Object {
        $key = $_.Name.ToLowerInvariant()
        if (-not $byName.ContainsKey($key)) { $byName[$key] = New-Object System.Collections.Generic.List[System.IO.FileInfo] }
        $byName[$key].Add($_)
    }
Write-Host "Файлов в папке: $(($byName.Values | Measure-Object -Property Count -Sum).Sum)"

# ---------- Apprentice для зависимостей ----------

$apprentice = $null
if (-not $NoDependencies) {
    try {
        $apprentice = New-Object -ComObject Inventor.ApprenticeServerComponent
        Write-Host 'Apprentice запущен — зависимости сборок будут собраны.' -ForegroundColor Green
    } catch {
        Write-Warning "Apprentice (Inventor или Inventor View) не запустился — зависимости сборок собраны не будут. $($_.Exception.Message)"
    }
}

# Значение свойства COM или $null (у потерянной ссылки часть свойств пустая или недоступна).
function Get-ComValue($object, [string]$name) {
    try { return $object.$name } catch { return $null }
}

# Ссылки документа: путь (если известен) и имя файла. У ссылки, которую Apprentice не нашёл, полного пути
# может не быть — тогда имя берётся из относительного или логического имени: искать будем по имени.
function Get-References([string]$path) {
    $result = New-Object System.Collections.Generic.List[object]
    if ($null -eq $apprentice) { return ,$result }
    $doc = $null
    try {
        $doc = $apprentice.Open($path)
        $descriptors = $doc.File.ReferencedFileDescriptors
        $count = [int](Get-ComValue $descriptors 'Count')
        for ($i = 1; $i -le $count; $i++) {
            $descriptor = $descriptors.Item($i)
            $full = Get-ComValue $descriptor 'FullFileName'
            $candidates = @($full, (Get-ComValue $descriptor 'RelativeFileName'), (Get-ComValue $descriptor 'LogicalFileName'))
            $name = $null
            foreach ($candidate in $candidates) {
                if ($candidate) {
                    $leaf = [System.IO.Path]::GetFileName([string]$candidate)
                    if ($leaf) { $name = $leaf; break }
                }
            }
            if ($name) {
                $result.Add(@{ Path = $(if ($full) { [string]$full } else { $null }); Name = $name })
            } else {
                $script:problems.Add("Ссылка без имени в $path (номер $i)")
            }
        }
        Write-Host ("  {0}: зависимостей {1}" -f [System.IO.Path]::GetFileName($path), $count)
    } catch {
        $script:problems.Add("Не прочитаны зависимости: $path — $($_.Exception.Message)")
        Write-Warning "Не прочитаны зависимости: $path — $($_.Exception.Message)"
    } finally {
        if ($null -ne $doc) { try { $doc.Close() } catch { } }
    }
    return ,$result
}

# ---------- Поиск ----------

$found = [ordered]@{}           # имя (lower) → @{ File; Reason }
$missing = New-Object System.Collections.Generic.List[string]
$ambiguous = New-Object System.Collections.Generic.List[string]
$problems = New-Object System.Collections.Generic.List[string]
$queue = New-Object System.Collections.Generic.Queue[object]
$missingKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

foreach ($name in $names) { $queue.Enqueue(@{ Name = $name; Path = $null; Reason = 'из списка' }) }

while ($queue.Count -gt 0) {
    $item = $queue.Dequeue()
    $key = $item.Name.ToLowerInvariant()
    if ($found.Contains($key)) { continue }

    # И файлы списка, и зависимости ищутся только в папке поиска, по имени (там всё лежит вместе).
    $file = $null
    if ($byName.ContainsKey($key)) {
        $candidates = $byName[$key] | Sort-Object LastWriteTime -Descending
        $file = $candidates[0]
        if (@($candidates).Count -gt 1) {
            $ambiguous.Add("$($item.Name): взят $($file.FullName) (новее); ещё: " + (($candidates | Select-Object -Skip 1 | ForEach-Object { $_.FullName }) -join '; '))
        }
    }

    if ($null -eq $file) {
        # Одна и та же пропажа может прийти от нескольких сборок — в отчёте один раз.
        if ($missingKeys.Add($item.Name)) {
            $where = if ($item.Path) { "; сборка ссылалась на $($item.Path)" } else { '' }
            $missing.Add("$($item.Name) ($($item.Reason)$where)")
        }
        continue
    }

    $found[$key] = @{ File = $file; Reason = $item.Reason }
    $extension = $file.Extension.ToLowerInvariant()
    if ($apprentice -and $InventorExtensions -contains $extension) {
        foreach ($reference in (Get-References $file.FullName)) {
            $refName = $reference.Name
            if (-not $found.Contains($refName.ToLowerInvariant()) -and -not $missingKeys.Contains($refName)) {
                $queue.Enqueue(@{ Name = $refName; Path = $reference.Path; Reason = "зависимость $($file.Name)" })
            }
        }
    }
}

if ($null -ne $apprentice) {
    try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($apprentice) } catch { }
}

# ---------- Копирование и отчёт ----------

$report = New-Object System.Collections.Generic.List[string]
$report.Add("Сбор файлов: $(Get-Date -Format 'dd.MM.yyyy HH:mm')")
$report.Add("Папка компонентов: $Source")
$report.Add("Папка результата: $Target")
$report.Add("")

if (-not $DryRun) { New-Item -ItemType Directory -Force -Path $Target | Out-Null }

$copied = 0
$report.Add("НАЙДЕНО ($($found.Count)):")
foreach ($entry in $found.Values) {
    $file = $entry.File
    $report.Add("  $($file.Name)  ←  $($file.FullName)  [$($entry.Reason)]")
    if (-not $DryRun) {
        $destination = Join-Path $Target $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        # Копия — для работы: снимаем «только чтение», если был.
        $copy = Get-Item -LiteralPath $destination
        if ($copy.IsReadOnly) { $copy.IsReadOnly = $false }
        $copied++
    }
}

$report.Add("")
$report.Add("НЕ НАЙДЕНО ($($missing.Count)):")
foreach ($line in $missing) { $report.Add("  $line") }
if ($ambiguous.Count -gt 0) {
    $report.Add("")
    $report.Add("НЕСКОЛЬКО ФАЙЛОВ С ОДНИМ ИМЕНЕМ ($($ambiguous.Count)):")
    foreach ($line in $ambiguous) { $report.Add("  $line") }
}
if ($problems.Count -gt 0) {
    $report.Add("")
    $report.Add("ПРОБЛЕМЫ ($($problems.Count)):")
    foreach ($line in $problems) { $report.Add("  $line") }
}

$report | ForEach-Object { Write-Host $_ }
Write-Host ""
if ($DryRun) {
    Write-Host "Пробный запуск: ничего не скопировано. Найдено: $($found.Count), не найдено: $($missing.Count)." -ForegroundColor Yellow
} else {
    $reportPath = Join-Path $Target '_отчёт.txt'
    $report | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "Скопировано: $copied, не найдено: $($missing.Count). Отчёт: $reportPath" -ForegroundColor Green
}
