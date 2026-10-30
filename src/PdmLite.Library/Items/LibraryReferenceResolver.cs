namespace PdmLite.Library.Items;

/// <summary>
/// Решение, чем заменить ссылку документа Inventor, которую он не может найти: если это компонент
/// библиотеки — его текущий файл. Ссылки на существующие файлы и на файлы проекта не трогаются.
/// </summary>
public static class LibraryReferenceResolver
{
    /// <param name="index">Текущие и прежние пути компонентов.</param>
    /// <param name="libraryRoot">Папка библиотеки.</param>
    /// <param name="reference">Ссылка, как её хранит документ: полный путь или путь внутри библиотеки проекта.</param>
    /// <param name="libraryName">Имя библиотеки проекта Inventor, если ссылка на библиотеку.</param>
    /// <param name="ourLibraryName">Под каким именем наша библиотека подключается к проектам.</param>
    /// <param name="fileExists">Проверка файла (в тестах — подмена).</param>
    /// <returns>Полный путь компонента библиотеки или <c>null</c> — пусть Inventor ищет сам.</returns>
    public static string? Resolve(LibraryPathIndex index, string libraryRoot, string reference, string? libraryName,
        string ourLibraryName, Func<string, bool> fileExists)
    {
        string Full(string relative) => Path.Combine(libraryRoot, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

        bool missing;
        if (!string.IsNullOrEmpty(libraryName))
        {
            // Ссылка внутри библиотеки проекта: наша — проверим, есть ли файл; чужая — не знаем.
            var ours = string.Equals(libraryName, ourLibraryName, StringComparison.OrdinalIgnoreCase);
            if (ours && fileExists(Full(reference)))
                return null;
            missing = ours;
        }
        else if (Path.IsPathRooted(reference))
        {
            if (fileExists(reference))
                return null;
            missing = true;
        }
        else
        {
            missing = false; // путь относительно проекта — проверить не можем, ищем только по старым путям
        }

        if (index.Find(reference, missing) is not { } relative)
            return null;
        var full = Full(relative);
        return fileExists(full) && !string.Equals(full, reference, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Нужно ли перечитать индекс при промахе (каталог могли изменить в Explorer — другой процесс).</summary>
    public static bool ShouldReload(string reference, string? libraryName, string ourLibraryName) =>
        Path.IsPathRooted(reference) || string.Equals(libraryName, ourLibraryName, StringComparison.OrdinalIgnoreCase);
}
