using System.Security.Cryptography;
using System.Text.Json;
using PdmLite.Core.IO;
using PdmLite.Data;
using PdmLite.Hosting;
using PdmLite.Library.Storage;

namespace PdmLite.Installation;

/// <summary>Durable first-install intent. Existing settings always take precedence over a bundled seed.</summary>
internal sealed class SetupState
{
    internal const int Port = 55439;
    private readonly string folder;
    private string PendingPath => Path.Combine(folder, "setup-pending.json");
    public DatabaseSettings? Existing { get; }
    public PendingSetup? Pending { get; }

    private SetupState(string folder, DatabaseSettings? existing, PendingSetup? pending)
        => (this.folder, Existing, Pending) = (folder, existing, pending);

    public static SetupState Open(string folder, string root)
    {
        Directory.CreateDirectory(folder);
        var databasePath = Path.Combine(folder, "database.json");
        if (File.Exists(databasePath))
        {
            var settings = Read<DatabaseSettings>(databasePath);
            if (!settings.IsConfigured) throw new InvalidDataException("Настройки подключения неполные. Настройте подключение в PdmLite.");
            return new SetupState(folder, settings, null);
        }
        var pendingPath = Path.Combine(folder, "setup-pending.json");
        if (File.Exists(pendingPath))
        {
            var pending = Read<PendingSetup>(pendingPath);
            if (!pending.Admin.IsConfigured || !pending.Database.IsConfigured || string.IsNullOrWhiteSpace(pending.Root))
                throw new InvalidDataException("Повреждены настройки незавершённой установки. Существующие файлы сохранены.");
            if (!string.Equals(Path.GetFullPath(pending.Root), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Продолжите незавершённую установку в папку: " + pending.Root);
            return new SetupState(folder, null, pending);
        }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Выбранная папка библиотеки не пустая. Выберите пустую папку. Существующие файлы сохранены.");
        var fresh = new PendingSetup { Root = Path.GetFullPath(root) };
        fresh.Admin.SetPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        fresh.Database.SetPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        var state = new SetupState(folder, null, fresh);
        state.Save();
        return state;
    }

    public void Save() => AtomicFile.WriteAllText(PendingPath, JsonSerializer.Serialize(Pending, JsonSettings.Options));

    public void Complete(LibrarySettings library)
    {
        if (Pending is not { Restored: true } pending) throw new InvalidOperationException("Библиотека ещё не восстановлена.");
        // Connection settings are the commit marker, published only after all prerequisites.
        Write("local-postgres-admin.json", pending.Admin);
        Write("library.json", library);
        Write("database.json", pending.Database);
        File.Delete(PendingPath);
    }

    private void Write<T>(string name, T value) => AtomicFile.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(value, JsonSettings.Options));

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonSettings.Options)
        ?? throw new InvalidDataException("Не удалось прочитать " + Path.GetFileName(path) + ". Существующие настройки сохранены.");
}

internal sealed class PendingSetup
{
    public string Root { get; set; } = "";
    public DatabaseSettings Admin { get; set; } = new() { Host = "127.0.0.1", Port = SetupState.Port, Database = "postgres", Username = "postgres" };
    public DatabaseSettings Database { get; set; } = new() { Host = "127.0.0.1", Port = SetupState.Port };
    public bool Restored { get; set; }
}
