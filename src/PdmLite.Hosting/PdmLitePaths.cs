namespace PdmLite.Hosting;

public static class PdmLitePaths
{
    /// <summary><c>%AppData%\PdmLite</c> — настройки пользователя.</summary>
    public static string UserSettingsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdmLite");

    /// <summary><c>%LocalAppData%\PdmLite\logs</c> — журналы.</summary>
    public static string LogsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmLite", "logs");
}
