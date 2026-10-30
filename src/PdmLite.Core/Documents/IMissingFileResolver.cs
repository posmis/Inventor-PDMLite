namespace PdmLite.Core.Documents;

/// <summary>
/// Подставляет файл вместо ссылки, которую документ Inventor не может найти (например, компонент
/// библиотеки переехал после изменения обозначения). Модули регистрируют реализации в DI.
/// Вызывается в главном потоке Inventor при открытии каждого файла — должен отвечать мгновенно.
/// </summary>
public interface IMissingFileResolver
{
    /// <param name="reference">Ссылка, как её хранит документ: полный путь или путь внутри библиотеки проекта.</param>
    /// <param name="libraryName">Имя библиотеки проекта Inventor, если ссылка на библиотеку, иначе <c>null</c>.</param>
    /// <returns>Полный путь найденного файла или <c>null</c> — пусть Inventor ищет сам.</returns>
    string? Resolve(string reference, string? libraryName);
}

/// <summary>Ссылка документа подменена найденным файлом.</summary>
public sealed record MissingFileResolved(string Reference, string ResolvedPath);
