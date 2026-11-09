using PdmLite.Library.Items;
using PdmLite.Modules.Library.Checks;

namespace PdmLite.Library.Tests;

public class LibraryPathCacheTests
{
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static LibraryPathIndex Snapshot(string path) => new([(Guid.NewGuid(), path, "Деталь")], []);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeDuringLoad_DiscardsOldResultOrError_AndCoalescesEvents(bool failOld)
    {
        var first = Signal<LibraryPathIndex>();
        var second = Signal<LibraryPathIndex>();
        var started = Signal<bool>();
        var restarted = Signal<bool>();
        var now = new DateTime(2026, 1, 1);
        var loads = 0;
        var errors = new List<Exception>();
        await using var cache = new LibraryPathCache(_ =>
        {
            if (Interlocked.Increment(ref loads) == 1) { started.SetResult(true); return first.Task; }
            restarted.SetResult(true);
            return second.Task;
        }, errors.Add, () => now);

        var pending = cache.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100; i++) Assert.Same(pending, cache.RefreshAsync());
        if (failOld) first.SetException(new IOException("old failure"));
        else first.SetResult(Snapshot("old/Деталь.ipt"));
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(cache.Current); // Устаревший снимок не публикуется даже на время.
        var latest = Snapshot("new/Деталь.ipt");
        second.SetResult(latest);
        await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(latest, cache.Get(false));
        Assert.Equal(2, loads);
        Assert.Empty(errors);
        Assert.Equal(TimeSpan.Zero, cache.Age);
        now = now.AddSeconds(25);
        Assert.Equal(TimeSpan.FromSeconds(25), cache.Age);
    }

    [Fact]
    public async Task ChangeDuringFollowUpLoad_IsNotLost()
    {
        var started = Enumerable.Range(0, 3).Select(_ => Signal<bool>()).ToArray();
        var results = Enumerable.Range(0, 3).Select(_ => Signal<LibraryPathIndex>()).ToArray();
        var calls = 0;
        await using var cache = new LibraryPathCache(_ =>
        {
            var call = Interlocked.Increment(ref calls) - 1;
            started[call].SetResult(true);
            return results[call].Task;
        }, _ => { });
        var pending = cache.RefreshAsync();
        for (var i = 0; i < 2; i++)
        {
            await started[i].Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(pending, cache.RefreshAsync());
            results[i].SetResult(Snapshot($"stale{i}.ipt"));
        }
        await started[2].Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(cache.Current);
        var latest = Snapshot("latest.ipt");
        results[2].SetResult(latest);
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(latest, cache.Current);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task TimedOutWait_DoesNotStartAnotherLoad_AndLateSuccessClearsBackoff()
    {
        var started = Signal<bool>();
        var result = Signal<LibraryPathIndex>();
        var calls = 0;
        var latest = Snapshot("latest.ipt");
        await using var cache = new LibraryPathCache(_ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(latest);
            started.SetResult(true);
            return result.Task;
        }, _ => { });
        var pending = cache.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await Task.Run(() => cache.Get(false)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(cache.Get(true)); // Повторный файл не ждёт ту же загрузку ещё три секунды.
        Assert.Equal(1, calls);
        result.SetResult(latest);
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(latest, cache.Get(true));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedRefresh_PreservesSnapshotAndAge_BackoffExpires_AndSuccessClearsFailure()
    {
        var now = new DateTime(2026, 1, 1);
        var initial = Snapshot("initial.ipt");
        var latest = Snapshot("latest.ipt");
        var calls = 0;
        var errors = new List<Exception>();
        await using var cache = new LibraryPathCache(_ => ++calls switch
        {
            1 => Task.FromResult(initial),
            2 => Task.FromException<LibraryPathIndex>(new IOException("offline")),
            _ => Task.FromResult(latest)
        }, errors.Add, () => now);
        await cache.RefreshAsync();
        now = now.AddSeconds(30);
        await cache.RefreshAsync();
        Assert.Same(initial, cache.Get(true));
        Assert.Equal(2, calls);
        Assert.Single(errors);
        Assert.Equal(TimeSpan.FromSeconds(30), cache.Age);
        now = now.AddMinutes(1);
        Assert.Same(latest, cache.Get(true));
        Assert.Equal(3, calls);
        Assert.Equal(TimeSpan.Zero, cache.Age);
        Assert.Same(latest, cache.Get(true));
        Assert.Equal(4, calls); // Успех снимает блокировку повторных загрузок.
    }

    [Fact]
    public async Task ExplicitChange_RetriesEvenDuringFailureBackoff()
    {
        var calls = 0;
        var latest = Snapshot("latest.ipt");
        await using var cache = new LibraryPathCache(_ => ++calls == 1
            ? Task.FromException<LibraryPathIndex>(new IOException("offline"))
            : Task.FromResult(latest), _ => { });
        await cache.RefreshAsync();
        Assert.Null(cache.Get(false));
        Assert.Equal(1, calls);
        await cache.RefreshAsync();
        Assert.Same(latest, cache.Get(false));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Get_JoinsInProgressRefresh_WithoutRequestingAnotherLoad()
    {
        var started = Signal<bool>();
        var loaded = Signal<LibraryPathIndex>();
        var calls = 0;
        await using var cache = new LibraryPathCache(_ =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult(true);
            return loaded.Task;
        }, _ => { });
        var pending = cache.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var read = Task.Run(() => cache.Get(false));
        var latest = Snapshot("latest.ipt");
        loaded.SetResult(latest);
        Assert.Same(latest, await read.WaitAsync(TimeSpan.FromSeconds(10)));
        await pending;
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_CancelsLoad_RejectsLatePublication_AndCanBeRepeated(bool observesCancellation)
    {
        var started = Signal<CancellationToken>();
        var completed = Signal<LibraryPathIndex>();
        var cancelled = Signal<bool>();
        var calls = 0;
        var errors = new List<Exception>();
        var cache = new LibraryPathCache(async ct =>
        {
            calls++;
            started.SetResult(ct);
            return observesCancellation ? await completed.Task.WaitAsync(ct) : await completed.Task;
        }, errors.Add);
        var pending = cache.RefreshAsync();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var registration = token.Register(() => cancelled.TrySetResult(true));
        cache.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = cache.DisposeAsync().AsTask();
        completed.TrySetResult(Snapshot("late.ipt"));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await pending;
        await cache.DisposeAsync();
        await cache.RefreshAsync();
        Assert.Null(cache.Get(true));
        Assert.Empty(errors);
        Assert.Equal(1, calls);
    }
}
