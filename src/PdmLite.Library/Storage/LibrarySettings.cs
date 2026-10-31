namespace PdmLite.Library.Storage;

/// <summary>Настройки библиотеки (<c>%AppData%\PdmLite\library.json</c>).</summary>
public sealed class LibrarySettings
{
    public const string Name = "library";

    /// <summary>Папка библиотеки. Подключается в проекты Inventor как библиотека «PdmLite».</summary>
    public string RootFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PdmLite", "Library");

    /// <summary>Предупреждать при вставке компонента из Content Center (и предлагать замену из библиотеки).</summary>
    public bool WarnOnContentCenter { get; set; } = true;

    /// <summary>Папка резервных копий библиотеки (ZIP-архивы).</summary>
    public string BackupFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PdmLite", "Backups");

    /// <summary>Сколько автоматических копий хранить (ручные не удаляются).</summary>
    public int BackupKeep { get; set; } = 10;

    /// <summary>Автоматическая копия раз в столько дней (при запуске Explorer); 0 — не делать.</summary>
    public int BackupIntervalDays { get; set; } = 7;
}
