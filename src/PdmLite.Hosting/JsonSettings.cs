using PdmLite.Core.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PdmLite.Hosting;

/// <summary>
/// Хранение настроек пользователя в <c>%AppData%\PdmLite\{name}.json</c>.
/// </summary>
public sealed class JsonSettings<T> where T : class, new()
{
    private readonly string _path;

    public JsonSettings(string name, string? folder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _path = ContainedPath.Resolve(folder ?? PdmLitePaths.UserSettingsFolder, name + ".json");
        Value = Load();
    }

    public T Value { get; private set; }

    public string FilePath => _path;

    public T Reload() => Value = Load();

    public void Save()
    {
        // Сериализация до записи: её ошибка не должна затронуть предыдущий файл.
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Value, JsonSettings.Options));
    }

    private T Load()
    {
        try
        {
            if (!File.Exists(_path)) return new T();
            using var stream = AtomicFile.OpenRead(_path);
            return JsonSerializer.Deserialize<T>(stream, JsonSettings.Options) ?? new T();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Повреждённый файл настроек не должен мешать запуску.
            return new T();
        }
    }
}

public static class JsonSettings
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}
