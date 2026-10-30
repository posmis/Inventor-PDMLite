using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PdmLite.Data;

public interface IPdmDatabase
{
    /// <summary>Подключение настроено (есть сервер, база, пользователь и пароль).</summary>
    bool IsConfigured { get; }

    DatabaseSettings Settings { get; }

    /// <summary>Применяет новые настройки подключения (после проверки и сохранения).</summary>
    void Configure(DatabaseSettings settings);

    PdmDbContext CreateContext();

    /// <summary>Создаёт или обновляет структуру базы (миграции). Безопасно вызывать при каждом запуске.</summary>
    Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>Проверка подключения; при ошибке — понятное сообщение.</summary>
    Task<(bool Ok, string Message)> TestAsync(DatabaseSettings settings, CancellationToken cancellationToken = default);
}

public sealed class PdmDatabase(DatabaseSettings settings, ILoggerFactory loggerFactory) : IPdmDatabase
{
    private DatabaseSettings _settings = settings;
    private readonly SemaphoreSlim _migrateGate = new(1, 1);
    private bool _migrated;

    public bool IsConfigured => _settings.IsConfigured;
    public DatabaseSettings Settings => _settings;

    public void Configure(DatabaseSettings settings)
    {
        _settings = settings;
        _migrated = false;
    }

    public PdmDbContext CreateContext() => new(BuildOptions(_settings.BuildConnectionString(), loggerFactory));

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (_migrated)
            return;
        await _migrateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrated)
                return;
            await using var db = CreateContext();
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            _migrated = true;
        }
        finally
        {
            _migrateGate.Release();
        }
    }

    public async Task<(bool Ok, string Message)> TestAsync(DatabaseSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(settings.BuildConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("select version()", connection);
            var version = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return (true, $"Подключение установлено: {version?.Split(',')[0]}");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidPassword)
        {
            return (false, "Неверный пользователь или пароль.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return (false, $"База данных «{settings.Database}» не найдена на сервере.");
        }
        catch (NpgsqlException ex) when (ex.InnerException is System.Net.Sockets.SocketException or TimeoutException)
        {
            return (false, $"Сервер {settings.Host}:{settings.Port} не отвечает. Проверьте, что PostgreSQL запущен.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static DbContextOptions<PdmDbContext> BuildOptions(string connectionString, ILoggerFactory? loggerFactory = null)
    {
        var builder = new DbContextOptionsBuilder<PdmDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__migrations", PdmDbContext.LibrarySchema))
            .UseSnakeCaseNamingConvention();
        if (loggerFactory is not null)
            builder.UseLoggerFactory(loggerFactory);
        return builder.Options;
    }
}
