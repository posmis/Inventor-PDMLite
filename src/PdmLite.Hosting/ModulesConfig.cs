using System.Text.Json;

namespace PdmLite.Hosting;

/// <summary>
/// Файл <c>modules.json</c>: включение и отключение модулей.
/// Модули, которых нет в файле, считаются включёнными.
/// </summary>
public sealed class ModulesConfig
{
    public const string FileName = "modules.json";

    public List<ModuleEntry> Modules { get; set; } = [];

    public bool IsEnabled(string moduleId) =>
        Modules.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase))?.Enabled ?? true;

    /// <summary>Ошибка чтения файла (тогда включены все модули).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadError { get; private set; }

    /// <summary>
    /// Читает modules.json. Повреждённый файл не мешает запуску: включаются все модули,
    /// причина — в <see cref="LoadError"/>.
    /// </summary>
    public static ModulesConfig Load(string path)
    {
        if (!File.Exists(path))
            return new ModulesConfig();

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ModulesConfig>(stream, JsonSettings.Options) ?? new ModulesConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ModulesConfig { LoadError = $"{path}: {ex.Message}" };
        }
    }

    public sealed class ModuleEntry
    {
        public string Id { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }
}
