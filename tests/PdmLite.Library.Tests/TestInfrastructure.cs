using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PdmLite.Core.Documents;
using PdmLite.Data;

namespace PdmLite.Library.Tests;

/// <summary>
/// Тест с настоящим PostgreSQL. Выполняется, если задана переменная PDMLITE_TEST_PG
/// (подключение с правом создавать базы), например
/// <c>Host=localhost;Username=pdmlite;Password=pdmlite</c>. Иначе пропускается.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(TestDatabase.AdminConnectionString))
            Skip = "Нет PostgreSQL для тестов: задайте переменную PDMLITE_TEST_PG.";
    }
}

/// <summary>Отдельная временная база на тест.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public static string? AdminConnectionString => Environment.GetEnvironmentVariable("PDMLITE_TEST_PG");

    private readonly string _name = "pdmlite_t_" + Guid.NewGuid().ToString("N")[..12];

    public PdmDatabase Database { get; private set; } = null!;

    public static async Task<TestDatabase> CreateAsync()
    {
        var db = new TestDatabase();
        await using (var connection = new NpgsqlConnection(AdminConnection()))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {db._name}", connection).ExecuteNonQueryAsync();
        }

        var admin = new NpgsqlConnectionStringBuilder(AdminConnectionString);
        var settings = new DatabaseSettings { Host = admin.Host!, Port = admin.Port, Database = db._name, Username = admin.Username! };
        settings.SetPassword(admin.Password ?? "");
        db.Database = new PdmDatabase(settings, NullLoggerFactory.Instance);
        return db;
    }

    /// <summary>Служебная база для CREATE/DROP DATABASE (по умолчанию postgres).</summary>
    private static string AdminConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString);
        builder.Database ??= "postgres";
        return builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(AdminConnection());
        await connection.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)", connection).ExecuteNonQueryAsync();
    }
}

/// <summary>Свойства файлов в памяти вместо Apprentice. Запись дописывает байт в файл (меняется хеш).</summary>
public sealed class FakePropertyStore : IDocumentPropertyStore
{
    public Dictionary<string, DocumentProperties> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DocumentPropertyChanges> Written { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IReadOnlyDictionary<string, string>> Relinked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? FailWriteFor { get; set; }
    public int WriteCount { get; private set; }

    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue(path, out var p) ? p : new DocumentProperties());

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default)
    {
        if (FailWriteFor is not null && path.Contains(FailWriteFor))
            throw new IOException("Файл повреждён");
        Written[path] = changes;
        WriteCount++;
        File.AppendAllText(path, "*");
        // Как в настоящем файле: следующее чтение видит записанное.
        var current = Files.TryGetValue(path, out var p) ? p : new DocumentProperties();
        var custom = new Dictionary<string, string>(current.Custom);
        foreach (var (name, value) in changes.Custom)
            custom[name] = value;
        Files[path] = current with
        {
            PartNumber = changes.PartNumber ?? current.PartNumber,
            Description = changes.Description ?? current.Description,
            Title = changes.Title ?? current.Title,
            Custom = custom,
            IsContentCenter = current.IsContentCenter && !changes.RemoveContentCenterLink,
        };
        return Task.CompletedTask;
    }

    public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default)
    {
        Relinked[path] = map;
        return Task.CompletedTask;
    }
}

/// <summary>Временная папка с исходными файлами и папкой библиотеки.</summary>
public sealed class TempFolders : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("pdmlite-lib-").FullName;
    public string Source => Path.Combine(Root, "Стандартные изделия");
    public string Library => Path.Combine(Root, "Library");

    public string AddFile(string relative, string content)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, true);
    }
}
