namespace PdmLite.Hosting;

/// <summary>
/// Настройки, общие для плагина и Explorer (<c>%AppData%\PdmLite\settings.json</c>).
/// </summary>
public sealed class SharedSettings
{
    public const string Name = "settings";

    /// <summary>
    /// Путь к PdmLite.Explorer.exe. Explorer записывает его при каждом запуске,
    /// чтобы плагин мог его найти.
    /// </summary>
    public string? ExplorerPath { get; set; }
}
