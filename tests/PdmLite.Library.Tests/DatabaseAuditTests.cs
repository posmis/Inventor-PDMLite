using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PdmLite.Data;

namespace PdmLite.Library.Tests;

public class DatabaseAuditTests
{
    [Fact]
    public async Task TestConnection_CancellationIsNotConvertedToFailureMessage()
    {
        var settings = new DatabaseSettings();
        var database = new PdmDatabase(settings, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.TestAsync(settings, cts.Token));
    }

    [PostgresFact]
    public async Task ConfigureDuringBlockedMigration_MigratesNewDatabaseBeforeMarkingItReady()
    {
        await using var first = await TestDatabase.CreateAsync();
        await using var second = await TestDatabase.CreateAsync();
        await first.Database.MigrateAsync();
        var racing = new PdmDatabase(first.Database.Settings, NullLoggerFactory.Instance);
        await using var blocker = new NpgsqlConnection(first.Database.Settings.BuildConnectionString());
        await blocker.OpenAsync(); await using var tx = await blocker.BeginTransactionAsync();
        await new NpgsqlCommand("LOCK TABLE library.__migrations IN ACCESS EXCLUSIVE MODE", blocker, tx).ExecuteNonQueryAsync();
        var migration = racing.MigrateAsync();
        try
        {
            await using var observer = new NpgsqlConnection(first.Database.Settings.BuildConnectionString());
            await observer.OpenAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%__migrations%')", observer);
                if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) break;
                await Task.Delay(10, timeout.Token);
            }
            racing.Configure(second.Database.Settings);
        }
        finally { await tx.RollbackAsync(); }
        await migration.WaitAsync(TimeSpan.FromSeconds(15));
        await using var context = second.Database.CreateContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(second.Database.Settings.Database, racing.Settings.Database);
    }

    [PostgresFact]
    public async Task ConcurrentMigrations_AndReconfigurationAreRepeatable()
    {
        await using var first = await TestDatabase.CreateAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => first.Database.MigrateAsync()));
        await using var second = await TestDatabase.CreateAsync();
        first.Database.Configure(second.Database.Settings);
        await first.Database.MigrateAsync();
        await using var context = second.Database.CreateContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task CanceledMigration_CanBeRetried()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.MigrateAsync(cts.Token));
        await db.Database.MigrateAsync();
        await using var context = db.Database.CreateContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }
}
