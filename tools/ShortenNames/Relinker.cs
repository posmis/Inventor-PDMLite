using System.Text;

namespace ShortenNames;

/// <summary>Ссылки документов Inventor (в программе — Apprentice, в тестах — подмена).</summary>
internal interface IDocumentLinks
{
    /// <summary>Ссылки документа: полный путь, а если его нет — относительное или логическое имя.</summary>
    IReadOnlyList<string> References(string path);

    /// <summary>
    /// Заменяет ссылки и сохраняет документ: по полному пути (<paramref name="byPath"/>), а если путь не распознан
    /// (длинный путь Inventor не читает) — по имени файла (<paramref name="byName"/>). Возвращает число замен.
    /// </summary>
    int Replace(string path, IReadOnlyDictionary<string, string> byPath, IReadOnlyDictionary<string, string> byName);
}

internal sealed record RelinkResult(int Renamed, int Documents, string BackupFolder, IReadOnlyList<string> Warnings);

/// <summary>Переименование не выполнено, всё возвращено (или — что не удалось вернуть — в тексте).</summary>
internal sealed class RelinkException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Переименование с перепривязкой: 1) кто ссылается — чтение ссылок всех документов папки; 2) копии документов,
/// которые изменятся на месте; 3) файлы копируются под новыми именами (старые пока остаются); 4) в документах
/// ссылки заменяются и проверяются; 5) старые файлы удаляются. Сбой на шагах 3–4 — всё возвращается.
/// </summary>
internal sealed class Relinker(IDocumentLinks links, Action<string> log)
{
    public string BackupRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmLite", "shorten-names");

    /// <summary>Документы папки, которые ссылаются на переименовываемые файлы (только чтение).</summary>
    public List<string> FindParents(string root, IReadOnlyList<NameChange> changes, List<string> warnings, CancellationToken ct = default)
    {
        var byPath = changes.Select(c => Key(c.From)).ToHashSet();
        var byName = UniqueNames(changes);
        var parents = new List<string>();
        var documents = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Shortener.IsInventorFile(f) && !Shortener.InOldVersions(root, f)).ToList();
        for (var i = 0; i < documents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var document = documents[i];
            log($"Ссылки {i + 1} из {documents.Count}: {Path.GetFileName(document)}");
            IReadOnlyList<string> references;
            try
            {
                references = links.References(document);
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(document)}: ссылки не прочитаны — {ex.Message}");
                continue;
            }
            if (references.Any(r => byPath.Contains(Key(r)) || byName.ContainsKey(Path.GetFileName(r))))
                parents.Add(document);
        }
        return parents;
    }

    public RelinkResult Run(string root, IReadOnlyList<NameChange> changes, CancellationToken ct = default)
    {
        changes = changes.Where(c => c.To is not null).ToList();
        var problems = new List<string>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (!File.Exists(change.From))
                problems.Add($"{change.FromName}: файл не найден.");
            if (File.Exists(change.To) && !change.To!.Equals(change.From, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{change.ToName}: такой файл уже есть.");
            if (!targets.Add(change.To!))
                problems.Add($"{change.ToName}: два файла получают одно имя.");
            if (change.ToName!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                problems.Add($"{change.ToName}: недопустимые символы в имени.");
        }
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var warnings = new List<string>();
        var parents = FindParents(root, changes, warnings, ct);
        var renamed = changes.ToDictionary(c => c.From, c => c.To!, StringComparer.OrdinalIgnoreCase);
        foreach (var parent in parents.Where(p => File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly)))
            problems.Add($"{Path.GetFileName(parent)}: файл только для чтения — снимите атрибут.");
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);
        WriteJournal(backup, root, changes, parents);

        var byPath = changes.ToDictionary(c => c.From, c => c.To!, StringComparer.OrdinalIgnoreCase);
        var byName = UniqueNames(changes);
        var copies = new List<(string Original, string Copy)>();
        var created = new List<string>();
        try
        {
            // Копии документов, изменяемых на месте.
            var inPlace = parents.Where(p => !renamed.ContainsKey(p)).ToList();
            for (var i = 0; i < inPlace.Count; i++)
            {
                var copy = Path.Combine(backup, $"{i + 1:000}{Path.GetExtension(inPlace[i])}");
                File.Copy(inPlace[i], copy);
                copies.Add((inPlace[i], copy));
                File.AppendAllText(Path.Combine(backup, "_журнал.txt"), $"{Path.GetFileName(copy)} = {inPlace[i]}{Environment.NewLine}");
            }

            foreach (var change in changes)
            {
                ct.ThrowIfCancellationRequested();
                log($"Копирование под новым именем: {change.ToName}");
                File.Copy(change.From, change.To!);
                created.Add(change.To!);
            }

            var keys = byPath.Keys.Select(Key).ToHashSet();
            foreach (var parent in parents)
            {
                ct.ThrowIfCancellationRequested();
                var target = renamed.GetValueOrDefault(parent, parent);
                log($"Ссылки в {Path.GetFileName(target)}");
                links.Replace(target, byPath, byName);
                var left = links.References(target).Where(r => keys.Contains(Key(r)) || byName.ContainsKey(Path.GetFileName(r))).ToList();
                if (left.Count > 0)
                    throw new InvalidOperationException($"в «{Path.GetFileName(target)}» не заменились ссылки: {string.Join(", ", left.Select(Path.GetFileName))}");
            }
        }
        catch (Exception ex)
        {
            var notRestored = new List<string>();
            foreach (var file in created)
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception deleteError)
                {
                    notRestored.Add($"не удалена копия {file}: {deleteError.Message}");
                }
            }
            foreach (var (original, copy) in copies)
            {
                try
                {
                    File.Copy(copy, original, overwrite: true);
                }
                catch (Exception restoreError)
                {
                    notRestored.Add($"не восстановлен {original}: {restoreError.Message}");
                }
            }
            File.AppendAllText(Path.Combine(backup, "_журнал.txt"), $"{Environment.NewLine}ОТМЕНЕНО: {ex.Message}{Environment.NewLine}"
                                                                      + string.Join(Environment.NewLine, notRestored));
            throw new RelinkException(notRestored.Count == 0
                ? $"Не выполнено: {ex.Message}. Все изменения отменены."
                : $"Не выполнено: {ex.Message}. Не всё удалось вернуть: {string.Join("; ", notRestored)}. Копии — в {backup}.", ex);
        }

        foreach (var change in changes)
        {
            try
            {
                File.SetAttributes(change.From, FileAttributes.Normal);
                File.Delete(change.From);
            }
            catch (Exception ex)
            {
                warnings.Add($"{change.FromName}: старый файл не удалён ({ex.Message}) — удалите вручную, ссылки уже на новый.");
            }
        }
        File.AppendAllText(Path.Combine(backup, "_журнал.txt"), $"{Environment.NewLine}ВЫПОЛНЕНО {DateTime.Now:dd.MM.yyyy HH:mm:ss}"
            + (warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, warnings)));
        return new RelinkResult(changes.Count, parents.Count, backup, warnings);
    }

    /// <summary>Старое имя файла → новый путь; имена, которые встречаются у нескольких файлов, по имени не заменяются.</summary>
    private static Dictionary<string, string> UniqueNames(IEnumerable<NameChange> changes) =>
        changes.Where(c => c.To is not null).GroupBy(c => c.FromName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().To!, StringComparer.OrdinalIgnoreCase);

    private static string Key(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // Ссылка, которую нельзя разобрать как путь, — как есть.
        }
        return path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    }

    private static void WriteJournal(string folder, string root, IReadOnlyList<NameChange> changes, IReadOnlyList<string> parents)
    {
        var text = new StringBuilder()
            .AppendLine($"Сокращение имён: {DateTime.Now:dd.MM.yyyy HH:mm:ss}, {Environment.UserName}")
            .AppendLine($"Папка: {root}")
            .AppendLine().AppendLine($"ПЕРЕИМЕНОВАНИЕ ({changes.Count}):");
        foreach (var change in changes)
            text.AppendLine($"  {change.From}").AppendLine($"    → {change.ToName}");
        text.AppendLine().AppendLine($"ССЫЛКИ ОБНОВЛЯЮТСЯ В ({parents.Count}):");
        foreach (var parent in parents)
            text.AppendLine($"  {parent}");
        text.AppendLine().AppendLine("Копии документов, изменённых на месте (файл копии = исходный документ):");
        File.WriteAllText(Path.Combine(folder, "_журнал.txt"), text.ToString(), new UTF8Encoding(true));
    }
}
