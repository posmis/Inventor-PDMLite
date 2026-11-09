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
    private sealed record Configuration(DatabaseSettings Settings, bool Migrated = false);
    private Configuration _configuration = new(settings.Copy());
    private readonly SemaphoreSlim _migrateGate = new(1, 1);

    public bool IsConfigured => Settings.IsConfigured;
    public DatabaseSettings Settings => Volatile.Read(ref _configuration).Settings.Copy();

    public void Configure(DatabaseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _configuration, new Configuration(settings.Copy()));
    }

    public PdmDbContext CreateContext() => new(BuildOptions(Settings.BuildConnectionString(), loggerFactory));

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _configuration).Migrated)
            return;
        await _migrateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configuration = Volatile.Read(ref _configuration);
                if (configuration.Migrated) return;
                await using var db = new PdmDbContext(BuildOptions(configuration.Settings.BuildConnectionString(), loggerFactory));
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                // Configure во время миграции не должен пометить новую базу как уже обновлённую.
                if (ReferenceEquals(Interlocked.CompareExchange(ref _configuration,
                    configuration with { Migrated = true }, configuration), configuration)) return;
            }
        }
        finally
        {
            _migrateGate.Release();
        }
    }

    public async Task<(bool Ok, string Message)> TestAsync(DatabaseSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException) { throw; }
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
