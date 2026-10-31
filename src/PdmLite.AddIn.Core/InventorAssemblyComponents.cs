using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Threading;

namespace PdmLite.AddIn.Core;

/// <summary>Состав активной сборки и замена компонентов через Inventor API.</summary>
public sealed class InventorAssemblyComponents(Inv.Application inventor, IUiDispatcher ui, ILogger<InventorAssemblyComponents> logger)
    : IAssemblyComponents
{
    public Task<AssemblyInspection> InspectActiveAsync(CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() =>
        {
            var assembly = ActiveAssembly();
            var components = new Dictionary<string, (string? PartNumber, bool ContentCenter, int Count)>(StringComparer.OrdinalIgnoreCase);

            foreach (var occurrence in AllOccurrences(assembly.ComponentDefinition.Occurrences))
            {
                if (PathOf(occurrence) is not { } path)
                    continue;
                if (components.TryGetValue(path, out var known))
                {
                    components[path] = known with { Count = known.Count + 1 };
                    continue;
                }

                var document = Safe(() => occurrence.Definition?.Document as Inv._Document, null);
                components[path] = (InventorProperties.PartNumber(document), IsContentCenter(occurrence), 1);
            }

            return new AssemblyInspection(assembly.FullFileName,
                components.Select(c => new AssemblyComponentInfo(c.Key, c.Value.PartNumber, c.Value.ContentCenter, c.Value.Count)).ToList());
        });

    public Task<ReplacementResult> ReplaceAsync(IReadOnlyList<ComponentReplacement> replacements, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() =>
        {
            var top = ActiveAssembly();
            var map = replacements.ToDictionary(r => r.OldPath, r => r.NewPath, StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            var modified = new List<string>();
            string? backupFolder = null;
            var replaced = 0;

            // Замена делается в той сборке, где компонент стоит непосредственно: в самой сборке и в каждой подсборке.
            foreach (var assembly in AssembliesOf(top))
            {
                var direct = assembly.ComponentDefinition.Occurrences.Cast<Inv.ComponentOccurrence>()
                    .Where(o => !o.Suppressed)
                    .Select(o => (Occurrence: o, Path: PathOf(o)))
                    .Where(o => o.Path is not null && map.ContainsKey(o.Path))
                    .GroupBy(o => o.Path!, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (direct.Count == 0)
                    continue;

                var file = assembly.FullFileName;
                if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))
                {
                    problems.Add($"{Path.GetFileName(file)}: файл только для чтения (например, сборка библиотеки) — не изменялась.");
                    continue;
                }

                if (File.Exists(file))
                {
                    backupFolder ??= CreateBackupFolder(top.FullFileName);
                    File.Copy(file, Path.Combine(backupFolder, Path.GetFileName(file)), overwrite: true);
                }

                var newPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in direct)
                {
                    try
                    {
                        // ReplaceAll = true: все вхождения этого файла в этой сборке.
                        group.First().Occurrence.Replace(map[group.Key], true);
                        newPaths.Add(map[group.Key]);
                        replaced++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Не удалось заменить {Old} в {Assembly}", group.Key, file);
                        problems.Add($"{Path.GetFileName(file)}: «{Path.GetFileName(group.Key)}» не заменён — {ex.Message}");
                    }
                }

                // Inventor оставляет старое имя вхождения (например, из Content Center) — даём имя по новому файлу.
                FixNames(assembly, newPaths, apply: true, logger);

                if (!modified.Contains(file))
                    modified.Add(file);
            }

            logger.LogInformation("Замена компонентов в {Assembly}: {Count}, резервные копии: {Backup}", top.FullFileName, replaced, backupFolder);
            return new ReplacementResult(replaced, modified, backupFolder, problems);
        });

    public Task<ComponentSelection> GetSelectionAsync(CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() =>
        {
            var assembly = ActiveAssembly();
            var selected = new List<string>();
            var components = new List<string>();
            var unsaved = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Consider(Inv._Document? document, List<string> target)
            {
                if (document is null || string.IsNullOrEmpty(document.FullFileName) || !seen.Add(document.FullFileName))
                    return;
                target.Add(document.FullFileName);
                if (document.Dirty)
                    unsaved.Add(document.FullFileName);
            }

            // В браузере и на экране выделяются вхождения (во вложенных сборках — их представители, proxy).
            foreach (var item in assembly.SelectSet)
                if (item is Inv.ComponentOccurrence occurrence && occurrence.Definition?.Document is Inv._Document document)
                    Consider(document, selected);

            foreach (var path in selected.ToList())
                if (Open(path) is Inv.AssemblyDocument sub)
                    foreach (Inv._Document child in sub.AllReferencedDocuments)
                        Consider(child, components);

            return new ComponentSelection(selected, components, unsaved);
        });

    public Task<IReadOnlyList<OccurrenceRename>> FixOccurrenceNamesAsync(IReadOnlyCollection<string> componentPaths, bool apply,
        CancellationToken cancellationToken = default) =>
        ui.InvokeAsync<IReadOnlyList<OccurrenceRename>>(() =>
        {
            var paths = componentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new List<OccurrenceRename>();
            foreach (var assembly in AssembliesOf(ActiveAssembly()))
            {
                // Сборки библиотеки — только для чтения, их имена не трогаем.
                var file = assembly.FullFileName;
                if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))
                    continue;
                result.AddRange(FixNames(assembly, paths, apply, logger));
            }
            return result;
        });

    /// <summary>
    /// Имена вхождений файлов <paramref name="paths"/> в сборке (одного уровня) — по имени файла
    /// (у библиотеки оно совпадает с обозначением): «Болт M8…:1». Обычные имена не меняются.
    /// </summary>
    internal static List<OccurrenceRename> FixNames(Inv.AssemblyDocument assembly, IReadOnlySet<string> paths, bool apply, ILogger logger)
    {
        var occurrences = assembly.ComponentDefinition.Occurrences.Cast<Inv.ComponentOccurrence>()
            .Select(o => (Occurrence: o, Name: Safe(() => o.Name, (string?)null)))
            .Where(o => o.Name is not null)
            .ToList();
        var candidates = occurrences
            .Select(o => (o.Occurrence, o.Name, Path: PathOf(o.Occurrence)))
            .Where(o => o.Path is not null && paths.Contains(o.Path))
            .Select(o => (o.Occurrence, Current: o.Name!, BaseName: Path.GetFileNameWithoutExtension(o.Path!)))
            .ToList();
        var plan = OccurrenceNaming.Plan(occurrences.Select(o => o.Name!), candidates.Select(c => (c.Current, c.BaseName)));

        var result = new List<OccurrenceRename>();
        foreach (var (oldName, newName) in plan)
        {
            result.Add(new OccurrenceRename(Path.GetFileName(assembly.FullFileName), oldName, newName));
            if (!apply)
                continue;
            try
            {
                candidates.First(c => c.Current == oldName).Occurrence.Name = newName;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось переименовать вхождение {Old} → {New}", oldName, newName);
            }
        }
        return result;
    }

    public Task SaveDocumentsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() =>
        {
            foreach (var path in paths)
                if (Open(path) is { } document)
                {
                    document.Save();
                    logger.LogInformation("Сохранён {Path}", path);
                }
        });

    /// <summary>Документ, уже открытый в Inventor (в том числе как компонент сборки), или <c>null</c>.</summary>
    private Inv._Document? Open(string path)
    {
        foreach (Inv._Document document in inventor.Documents)
            if (string.Equals(document.FullFileName, path, StringComparison.OrdinalIgnoreCase))
                return document;
        return null;
    }

    private Inv.AssemblyDocument ActiveAssembly() =>
        inventor.ActiveDocument as Inv.AssemblyDocument
        ?? throw new InvalidOperationException("Активный документ — не сборка. Откройте или активируйте сборку, которую нужно проверить.");

    /// <summary>
    /// Все вхождения на всех уровнях, кроме подавленных. Вхождение, свойства которого Inventor не отдаёт
    /// (виртуальное, не загруженное, повреждённое), пропускается — проверка остальных продолжается.
    /// </summary>
    private static IEnumerable<Inv.ComponentOccurrence> AllOccurrences(System.Collections.IEnumerable occurrences)
    {
        foreach (Inv.ComponentOccurrence occurrence in occurrences)
        {
            if (Safe(() => occurrence.Suppressed, true))
                continue;
            yield return occurrence;
            if (Safe(() => occurrence.SubOccurrences is { Count: > 0 } c ? c : null, null) is { } children)
                foreach (var child in AllOccurrences(children))
                    yield return child;
        }
    }

    /// <summary>Сама сборка и все её подсборки (каждая — один раз).</summary>
    private static IEnumerable<Inv.AssemblyDocument> AssembliesOf(Inv.AssemblyDocument top)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { top.FullFileName };
        yield return top;
        foreach (var occurrence in AllOccurrences(top.ComponentDefinition.Occurrences))
            if (Safe(() => occurrence.Definition?.Document as Inv.AssemblyDocument, null) is { } sub && seen.Add(sub.FullFileName))
                yield return sub;
    }

    /// <summary>Свойство Inventor API или <paramref name="fallback"/>, если Inventor его не отдаёт (COM-ошибка).</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return fallback;
        }
    }

    private static string? PathOf(Inv.ComponentOccurrence occurrence)
    {
        try
        {
            var path = occurrence.ReferencedDocumentDescriptor?.FullDocumentName;
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // виртуальный компонент — без файла
        }
    }

    internal static bool IsContentCenter(Inv.ComponentOccurrence occurrence)
    {
        try
        {
            return occurrence.Definition is Inv.PartComponentDefinition { IsContentMember: true };
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static string CreateBackupFolder(string assemblyPath)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmLite", "backups",
            $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileNameWithoutExtension(assemblyPath)}");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
