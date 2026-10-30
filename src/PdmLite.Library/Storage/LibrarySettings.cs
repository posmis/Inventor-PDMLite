namespace PdmLite.Library.Storage;

/// <summary>Настройки библиотеки (<c>%AppData%\PdmLite\library.json</c>).</summary>
public sealed class LibrarySettings
{
    public const string Name = "library";

    /// <summary>Папка библиотеки. Подключается в проекты Inventor как библиотека «PdmLite».</summary>
    public string RootFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PdmLite", "Library");
}
