using Npgsql;

namespace PdmLite.Library.Backup;

/// <summary>Восстановления одной БД не могут подменить маркер друг друга во время проверки COMMIT.</summary>
internal sealed class RestoreDatabaseGate(NpgsqlConnection connection, NpgsqlTransaction transaction,
    TimeSpan timeout, Action<Exception> report) : IAsyncDisposable
{
    private const long GateKey = 724897130052;

    public static async Task<RestoreDatabaseGate> EnterAsync(string connectionString, TimeSpan timeout,
        Action<Exception> report, CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(timeout);
            await connection.OpenAsync(stop.Token).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(stop.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction);
            command.Parameters.AddWithValue(GateKey);
            await command.ExecuteNonQueryAsync(stop.Token).ConfigureAwait(false);
            return new RestoreDatabaseGate(connection, transaction, timeout, report);
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task EnsureHeldAsync()
    {
        // Используется исходное соединение: его потеря не должна маскироваться переподключением.
        using var stop = new CancellationTokenSource(timeout);
        await using var command = new NpgsqlCommand("SELECT 1", connection, transaction);
        await command.ExecuteNonQueryAsync(stop.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // Освобождение вспомогательной транзакции не перекрывает результат восстановления.
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { report(ex); }
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { report(ex); }
    }
}
