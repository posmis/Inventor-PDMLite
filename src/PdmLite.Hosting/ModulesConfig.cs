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
        Modules?.FirstOrDefault(m => m is not null && string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase))?.Enabled ?? true;

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
            var config = JsonSerializer.Deserialize<ModulesConfig>(stream, JsonSettings.Options);
            if (config is null || config.Modules is null || config.Modules.Any(m => m is null || string.IsNullOrWhiteSpace(m.Id))
                || config.Modules.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Modules.Count)
                throw new JsonException("Некорректный список модулей.");
            return config;
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
