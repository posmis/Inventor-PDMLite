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
        _path = Path.Combine(folder ?? PdmLitePaths.UserSettingsFolder, name + ".json");
        Value = Load();
    }

    public T Value { get; private set; }

    public string FilePath => _path;

    public T Reload() => Value = Load();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Value, JsonSettings.Options));
        File.Move(tmp, _path, overwrite: true);
    }

    private T Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(_path), JsonSettings.Options) ?? new T()
                : new T();
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
