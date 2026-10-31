using PdmLite.Core.Domain;
using PdmLite.Core.Naming;

namespace PdmLite.Index.Operations;

/// <summary>Переименование или перемещение одного файла: откуда → куда (полные пути).</summary>
public sealed record FileMove(string From, string To)
{
    /// <summary>Меняется только регистр букв — тот же файл для Windows: ссылки не меняются.</summary>
    public bool IsCaseOnly => From.Equals(To, StringComparison.OrdinalIgnoreCase) && !From.Equals(To, StringComparison.Ordinal);
}

/// <summary>Что записать в iProperties файла (<c>null</c> — не менять).</summary>
/// <param name="Path">Файл — по пути до операции.</param>
public sealed record PropertyUpdate(string Path, string? PartNumber, string? Title);

/// <summary>Списки файлов для переименования и перемещения (без файловой системы, кроме перечня файлов папки).</summary>
public static class FileOperationRequests
{
    /// <summary>
    /// Связанные с моделью файлы в той же папке: тот же документ с другим кодом и расширением —
    /// по обозначению (<c>АБВГ.00.00.000СБ Жгут.dwg</c>, <c>…МЭ Жгут.dwg</c>, <c>….pdf</c>). Если имя модели не по
    /// правилу — файлы с тем же именем и другим расширением (<c>Кронштейн.idw</c>).
    /// </summary>
    public static IReadOnlyList<string> RelatedInFolder(string model, IEnumerable<string> folderFiles)
    {
        var stem = Path.GetFileNameWithoutExtension(model);
        var parsed = FileNameBuilder.TryParse(stem);
        return folderFiles
            .Where(f => !f.Equals(model, StringComparison.OrdinalIgnoreCase) && !IsTemporary(f))
            .Where(f => IsRelated(parsed, stem, Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance)
            .ToList();
    }

    /// <summary>Файл <paramref name="otherStem"/> — документ той же модели (по обозначению или по тому же имени).</summary>
    public static bool IsRelated(DocumentName? model, string modelStem, string otherStem)
    {
        if (model is null)
            return otherStem.Equals(modelStem, StringComparison.OrdinalIgnoreCase);
        return FileNameBuilder.TryParse(otherStem) is { } other && other.Designation.Equals(model.Designation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Новые имена: у каждого файла сохраняются его код документа и расширение, меняются обозначение
    /// и наименование. Файл не по правилу получает имя без кода. Файлы, имя которых не меняется, пропускаются.
    /// </summary>
    public static IReadOnlyList<FileMove> Rename(IEnumerable<string> files, string? designation, string? name)
    {
        var moves = new List<FileMove>();
        foreach (var file in files)
        {
            var code = FileNameBuilder.TryParse(Path.GetFileNameWithoutExtension(file))?.Code ?? "";
            var newName = FileNameBuilder.Compose(designation, code, name, Path.GetExtension(file));
            var target = Path.Combine(Path.GetDirectoryName(file)!, newName);
            if (!target.Equals(file, StringComparison.Ordinal))
                moves.Add(new FileMove(file, target));
        }
        return moves;
    }

    /// <summary>
    /// Перемещение в папку: файлы — в неё с теми же именами, папки — целиком (с подпапками), со своим именем.
    /// Папки <c>OldVersions</c> не переносятся.
    /// </summary>
    /// <param name="filesOfFolder">Все файлы папки с подпапками (в тестах — подмена).</param>
    /// <param name="isFolder">Путь — папка (по умолчанию проверка на диске).</param>
    public static IReadOnlyList<FileMove> Move(IEnumerable<string> paths, string targetFolder, Func<string, IEnumerable<string>> filesOfFolder,
        Func<string, bool>? isFolder = null)
    {
        isFolder ??= Directory.Exists;
        var moves = new List<FileMove>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Select(p => Path.TrimEndingDirectorySeparator(p)))
        {
            if (isFolder(path))
            {
                var destination = Path.Combine(targetFolder, Path.GetFileName(path));
                foreach (var file in filesOfFolder(path))
                {
                    var relative = Path.GetRelativePath(path, file);
                    if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IsOldVersions))
                        continue;
                    if (seen.Add(file))
                        moves.Add(new FileMove(file, Path.Combine(destination, relative)));
                }
            }
            else if (seen.Add(path))
            {
                moves.Add(new FileMove(path, Path.Combine(targetFolder, Path.GetFileName(path))));
            }
        }
        return moves.Where(m => !m.From.Equals(m.To, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Переименование папки: все её файлы (кроме OldVersions — их перенесёт сама папка) — под новым именем папки.</summary>
    public static (IReadOnlyList<FileMove> Files, FileMove Folder) RenameFolder(string folder, string newName, Func<string, IEnumerable<string>> filesOfFolder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        var target = Path.Combine(Path.GetDirectoryName(folder)!, newName.Trim());
        var files = new List<FileMove>();
        foreach (var file in filesOfFolder(folder))
        {
            var relative = Path.GetRelativePath(folder, file);
            if (!relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IsOldVersions))
                files.Add(new FileMove(file, Path.Combine(target, relative)));
        }
        return (files, new FileMove(folder, target));
    }

    /// <summary>Свойства для переименованных документов Inventor: обозначение (у чертежа — с кодом) и наименование.</summary>
    public static IReadOnlyList<PropertyUpdate> RenameProperties(IEnumerable<string> files, string? designation, string? name)
    {
        var result = new List<PropertyUpdate>();
        foreach (var file in files)
        {
            var kind = DocumentKinds.FromPath(file);
            if (kind is not (DocumentKind.Part or DocumentKind.Assembly or DocumentKind.Drawing or DocumentKind.Presentation))
                continue;
            var des = string.IsNullOrWhiteSpace(designation) ? null : designation.Trim();
            if (des is not null && kind == DocumentKind.Drawing
                && FileNameBuilder.TryParse(Path.GetFileNameWithoutExtension(file))?.Code is { Length: > 0 } code)
                des += code;
            var title = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (des is not null || title is not null)
                result.Add(new PropertyUpdate(file, des, title));
        }
        return result;
    }

    public static bool IsOldVersions(string segment) => segment.Equals("OldVersions", StringComparison.OrdinalIgnoreCase);

    /// <summary>Временные файлы Office/Inventor (<c>~$…</c>, <c>.lck</c>, <c>.bak</c>) — не документы.</summary>
    public static bool IsTemporary(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal)
               || Path.GetExtension(name).ToLowerInvariant() is ".lck" or ".bak" or ".tmp";
    }
}
