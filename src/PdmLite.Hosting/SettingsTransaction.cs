using System.Text.Json;
using PdmLite.Core.IO;

namespace PdmLite.Hosting;

/// <summary>Recoverable database/library pair. Readers and writers cooperate through a process-wide file lock.</summary>
public static class SettingsTransaction
{
    private const string JournalName = ".configuration-before.json";
    public static bool IsGrouped(string path) => Path.GetFileName(path) is "database.json" or "library.json";

    internal static IDisposable Enter(string folder)
    {
        Directory.CreateDirectory(folder);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return new FileStream(Path.Combine(folder, ".configuration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(20); }
            catch (IOException ex) { throw new TimeoutException("Настройки PdmLite заняты другим процессом.", ex); }
        }
    }
    public static void RecoverPending(string folder)
    {
        using var gate = Enter(folder);
        Recover(folder);
    }

    internal static void Recover(string folder)
    {
        var journal = Path.Combine(folder, JournalName);
        if (!File.Exists(journal)) return;
        try
        {
            var before = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(journal))
                ?? throw new InvalidDataException("Пустой журнал настроек.");
            if (before.Count != 2 || !before.ContainsKey("database.json") || !before.ContainsKey("library.json"))
                throw new InvalidDataException("Неполный журнал настроек.");
            foreach (var (name, text) in before)
            {
                if (name is not ("database.json" or "library.json")) throw new InvalidDataException("Недопустимый журнал настроек.");
                var path = Path.Combine(folder, name);
                if (text is null) { if (File.Exists(path)) File.Delete(path); }
                else if (!File.Exists(path) || File.ReadAllText(path) != text) AtomicFile.WriteAllText(path, text);
            }
            File.Delete(journal);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Не удалось восстановить согласованные настройки. Журнал сохранён: {journal}", ex);
        }
    }
    public static void SavePair<TFirst, TSecond>(JsonSettings<TFirst> first, TFirst firstValue,
        JsonSettings<TSecond> second, TSecond secondValue)
        where TFirst : class, new() where TSecond : class, new()
    {
        var folder = Path.GetDirectoryName(first.FilePath)!;
        if (!string.Equals(folder, Path.GetDirectoryName(second.FilePath), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(first.FilePath) != "database.json" || Path.GetFileName(second.FilePath) != "library.json")
            throw new ArgumentException("Транзакция предназначена для database.json и library.json одной папки.");
        // Serialization must succeed before touching either file or publishing mutable values.
        var firstText = JsonSerializer.Serialize(firstValue, JsonSettings.Options);
        var secondText = JsonSerializer.Serialize(secondValue, JsonSettings.Options);
        using var gate = Enter(folder);
        Recover(folder);
        var before = new Dictionary<string, string?>
        {
            ["database.json"] = File.Exists(first.FilePath) ? File.ReadAllText(first.FilePath) : null,
            ["library.json"] = File.Exists(second.FilePath) ? File.ReadAllText(second.FilePath) : null,
        };
        var journal = Path.Combine(folder, JournalName);
        AtomicFile.WriteAllText(journal, JsonSerializer.Serialize(before));
        try
        {
            AtomicFile.WriteAllText(first.FilePath, firstText);
            AtomicFile.WriteAllText(second.FilePath, secondText);
            File.Delete(journal);
        }
        catch (Exception failure)
        {
            try { Recover(folder); }
            catch (Exception rollback) { throw new AggregateException("Настройки не сохранены; восстановление неполное.", failure, rollback); }
            throw;
        }
        first.Accept(firstValue); second.Accept(secondValue);
    }
}
