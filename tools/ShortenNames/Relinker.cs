using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShortenNames;

/// <summary>Ссылки документов Inventor (в программе — Apprentice, в тестах — подмена).</summary>
internal interface IDocumentLinks
{
    /// <summary>
    /// Ссылки документа. У каждой — все варианты записи, какие есть: полный путь, относительное и логическое имя
    /// (у ссылки на файл с недопустимо длинным путём Inventor может не дать полного пути).
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>> Links(string path);

    /// <summary>
    /// Заменяет ссылки, для которых <paramref name="target"/> вернул новый путь, и сохраняет документ.
    /// Возвращает число замен.
    /// </summary>
    int Replace(string path, Func<IReadOnlyList<string>, string?> target);
}

/// <summary>Что будет сделано: документы со ссылками на переименовываемые файлы и что мешает.</summary>
/// <param name="Unreadable">Документы, ссылки которых не прочитаны (могут ссылаться на переименовываемые) — переименование запрещено.</param>
/// <param name="Unreferenced">Переименовываемые документы Inventor, на которые в папке никто не ссылается.</param>
internal sealed record RelinkPlan(IReadOnlyList<string> Parents, IReadOnlyList<string> Unreadable, IReadOnlyList<string> Unreferenced);

internal sealed record RelinkResult(int Renamed, int Documents, int References, string BackupFolder, IReadOnlyList<string> Warnings);

/// <summary>Переименование не выполнено, всё возвращено (или — что не удалось вернуть — в тексте).</summary>
internal sealed class RelinkException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Переименование с перепривязкой: 1) кто ссылается — чтение ссылок всех документов папки (<see cref="Prepare"/>);
/// 2) копии документов, которые изменятся на месте; 3) файлы копируются под новыми именами (старые пока остаются);
/// 4) в документах ссылки заменяются и проверяются; 5) старые файлы удаляются. Сбой на шагах 3–4 — всё возвращается.
/// Режим «только ссылки» — для уже переименованных файлов (по журналу прошлого запуска): шаги 3 и 5 пропускаются.
/// </summary>
internal sealed class Relinker(IDocumentLinks links, Action<string> log)
{
    public const string ChangesFile = "_переименования.json";

    /// <summary>Самый длинный путь документа, который Inventor (Apprentice) открывает и сохраняет.</summary>
    public const int MaxDocumentPath = 245;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Папка журналов и копий по умолчанию.</summary>
    public static string DefaultBackupRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmLite", "shorten-names");

    public string BackupRoot { get; set; } = DefaultBackupRoot;

    /// <summary>Только чтение: кто ссылается на переименовываемые файлы.</summary>
    public RelinkPlan Prepare(string root, IReadOnlyList<NameChange> changes, CancellationToken ct = default)
    {
        var match = new Matcher(changes);
        var parents = new List<string>();
        var unreadable = new List<string>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documents = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Shortener.IsInventorFile(f) && !Shortener.InOldVersions(root, f)).ToList();
        for (var i = 0; i < documents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var document = documents[i];
            log($"Ссылки {i + 1} из {documents.Count}: {document}");
            IReadOnlyList<IReadOnlyList<string>> references;
            try
            {
                references = links.Links(document);
            }
            catch (Exception ex)
            {
                log($"    НЕ ПРОЧИТАНЫ: {ex.Message}");
                unreadable.Add($"{document} — {ex.Message}");
                continue;
            }
            var found = false;
            foreach (var names in references)
            {
                var target = match.Target(names, Path.GetDirectoryName(document)!);
                log($"    {string.Join(" | ", names)}{(target is null ? "" : "   → " + Path.GetFileName(target))}");
                if (target is null)
                    continue;
                found = true;
                referenced.Add(target);
            }
            if (found)
                parents.Add(document);
        }

        var unreferenced = changes.Where(c => c.To is not null && Shortener.IsInventorFile(c.From)
                                              && !Path.GetExtension(c.From).Equals(".idw", StringComparison.OrdinalIgnoreCase)
                                              && !Path.GetExtension(c.From).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
                                              && !referenced.Contains(c.To!))
            .Select(c => c.From).ToList();
        log($"Документов со ссылками: {parents.Count}, не прочитано: {unreadable.Count}, без ссылок на них: {unreferenced.Count}");
        return new RelinkPlan(parents, unreadable, unreferenced);
    }

    /// <param name="relinkOnly">Файлы уже переименованы (старых нет, новые есть) — только заменить ссылки.</param>
    public RelinkResult Execute(string root, IReadOnlyList<NameChange> changes, RelinkPlan plan, bool relinkOnly = false, CancellationToken ct = default)
    {
        changes = changes.Where(c => c.To is not null).ToList();
        var problems = new List<string>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (relinkOnly)
            {
                if (!File.Exists(change.To))
                    problems.Add($"{change.ToName}: файл не найден.");
            }
            else
            {
                if (!File.Exists(change.From))
                    problems.Add($"{change.FromName}: файл не найден.");
                if (File.Exists(change.To) && !change.To!.Equals(change.From, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{change.ToName}: такой файл уже есть.");
            }
            if (!targets.Add(change.To!))
                problems.Add($"{change.ToName}: два файла получают одно имя.");
            if (change.ToName!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                problems.Add($"{change.ToName}: недопустимые символы в имени.");
        }
        if (plan.Unreadable.Count > 0)
            problems.Add("Не прочитаны ссылки документов (они могут ссылаться на переименовываемые файлы):" + Environment.NewLine
                         + string.Join(Environment.NewLine, plan.Unreadable.Take(10)));
        var renamed = relinkOnly ? new Dictionary<string, string>() : changes.ToDictionary(c => c.From, c => c.To!, StringComparer.OrdinalIgnoreCase);
        foreach (var parent in plan.Parents.Where(p => !renamed.ContainsKey(p) && p.Length > MaxDocumentPath))
            problems.Add($"{Path.GetFileName(parent)}: путь документа слишком длинный ({parent.Length}) — Inventor его не сохранит; " +
                         "уменьшите «Полный путь не длиннее», чтобы сократить и его.");
        foreach (var parent in plan.Parents.Where(p => File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly)))
            problems.Add($"{Path.GetFileName(parent)}: файл только для чтения — снимите атрибут.");
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);
        WriteJournal(backup, root, changes, plan.Parents, relinkOnly);

        var match = new Matcher(changes);
        var copies = new List<(string Original, string Copy)>();
        var created = new List<string>();
        var replacedTotal = 0;
        try
        {
            // Копии документов, изменяемых на месте.
            var inPlace = plan.Parents.Where(p => !renamed.ContainsKey(p)).ToList();
            for (var i = 0; i < inPlace.Count; i++)
            {
                var copy = Path.Combine(backup, $"{i + 1:000}{Path.GetExtension(inPlace[i])}");
                File.Copy(inPlace[i], copy);
                copies.Add((inPlace[i], copy));
                File.AppendAllText(Path.Combine(backup, "_журнал.txt"), $"  {Path.GetFileName(copy)} = {inPlace[i]}{Environment.NewLine}");
            }

            if (!relinkOnly)
            {
                foreach (var change in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    log($"Копирование под новым именем: {change.From} → {change.ToName}");
                    File.Copy(change.From, change.To!);
                    created.Add(change.To!);
                }
            }

            foreach (var parent in plan.Parents)
            {
                ct.ThrowIfCancellationRequested();
                var target = renamed.GetValueOrDefault(parent, parent);
                // Ссылки сравниваются относительно места, где документ лежал, когда их записали.
                var folder = Path.GetDirectoryName(parent)!;
                log($"Замена ссылок в {target}");
                var replaced = links.Replace(target, names => match.Target(names, folder));
                log($"    заменено: {replaced}");
                if (replaced == 0)
                    throw new InvalidOperationException($"в «{Path.GetFileName(target)}» ссылки не заменились (Inventor не узнал их) — подробности в журнале");
                var left = links.Links(target).Where(names => match.Target(names, folder) is not null).ToList();
                if (left.Count > 0)
                    throw new InvalidOperationException($"в «{Path.GetFileName(target)}» остались старые ссылки: "
                                                        + string.Join(", ", left.Select(n => Path.GetFileName(n[0]))));
                replacedTotal += replaced;
            }
        }
        catch (Exception ex)
        {
            log($"ОШИБКА: {ex.Message} — откат");
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

        var warnings = new List<string>();
        if (!relinkOnly)
        {
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
        }
        File.AppendAllText(Path.Combine(backup, "_журнал.txt"), $"{Environment.NewLine}ВЫПОЛНЕНО {DateTime.Now:dd.MM.yyyy HH:mm:ss}: заменено ссылок {replacedTotal}"
            + (warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, warnings)));
        return new RelinkResult(relinkOnly ? 0 : changes.Count, plan.Parents.Count, replacedTotal, backup, warnings);
    }

    /// <summary>Переименования из папки журнала прошлого запуска (файл <see cref="ChangesFile"/> или текст журнала).</summary>
    public static List<NameChange> LoadChanges(string journalFolderOrFile)
    {
        var folder = Directory.Exists(journalFolderOrFile) ? journalFolderOrFile : Path.GetDirectoryName(journalFolderOrFile)!;
        var json = Path.Combine(folder, ChangesFile);
        if (File.Exists(json))
            return JsonSerializer.Deserialize<List<NameChange>>(File.ReadAllText(json), Json) ?? [];

        // Журнал первых версий: «  <старый полный путь>» и следующей строкой «    → <новое имя>».
        var text = Path.Combine(folder, "_журнал.txt");
        var lines = File.ReadAllLines(text);
        var result = new List<NameChange>();
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            var arrow = lines[i + 1].TrimStart();
            if (!arrow.StartsWith("→ ", StringComparison.Ordinal) || !Path.IsPathRooted(lines[i].Trim()))
                continue;
            var from = lines[i].Trim();
            result.Add(new NameChange(from, Path.Combine(Path.GetDirectoryName(from)!, arrow[2..].Trim()), "по журналу"));
        }
        return result;
    }

    private static void WriteJournal(string folder, string root, IReadOnlyList<NameChange> changes, IReadOnlyList<string> parents, bool relinkOnly)
    {
        File.WriteAllText(Path.Combine(folder, ChangesFile), JsonSerializer.Serialize(changes, Json), new UTF8Encoding(true));
        var text = new StringBuilder()
            .AppendLine($"{(relinkOnly ? "Перепривязка ссылок по журналу" : "Сокращение имён")}: {DateTime.Now:dd.MM.yyyy HH:mm:ss}, {Environment.UserName}")
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

    /// <summary>
    /// Какой ссылке — какой новый путь. Варианты записи ссылки проверяются по очереди: полный путь (относительный —
    /// от папки документа), затем имя файла (только если имя у переименовываемых одно и ссылка не указывает
    /// на другой существующий файл).
    /// </summary>
    internal sealed class Matcher
    {
        private readonly Dictionary<string, string> _byKey = [];
        private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _newKeys = [];

        public Matcher(IEnumerable<NameChange> changes)
        {
            var list = changes.Where(c => c.To is not null).ToList();
            foreach (var change in list)
            {
                _byKey[Key(change.From)] = change.To!;
                _newKeys.Add(Key(change.To!));
            }
            foreach (var group in list.GroupBy(c => c.FromName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1))
                _byName[group.Key] = group.Single().To!;
        }

        public string? Target(IReadOnlyList<string> names, string documentFolder)
        {
            var otherExisting = false;
            foreach (var name in names)
            {
                var full = Full(name, documentFolder);
                if (full is null)
                    continue;
                var key = Key(full);
                if (_byKey.TryGetValue(key, out var target))
                    return target;
                if (_newKeys.Contains(key))
                    return null; // уже новая ссылка
                if (Path.IsPathRooted(name) && File.Exists(full))
                    otherExisting = true; // ссылка на другой существующий файл с тем же именем — не наша
            }
            if (otherExisting)
                return null;
            foreach (var name in names)
                if (_byName.TryGetValue(FileName(name), out var target))
                    return target;
            return null;
        }

        private static string? Full(string name, string folder)
        {
            try
            {
                return Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(folder, name));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FileName(string name) => name.Replace('/', '\\').Split('\\')[^1];
    }

    private static string Key(string path) => path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
}
