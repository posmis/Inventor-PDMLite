using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Events;
using PdmLite.Core.Lifetime;
using PdmLite.Core.Modularity;
using PdmLite.Core.UI;
using PdmLite.Hosting;

namespace PdmLite.Core.Tests;

public sealed class LifetimeRegressionTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("pdm-lifetime-").FullName;
    private sealed class Marker;
    private sealed class Poison;
    private sealed class Owned : IDisposable { public Owned() { } public bool Disposed; public void Dispose() => Disposed = true; }
    private sealed class Module(string id, Action<IServiceCollection>? configure = null, Action<IModuleContext>? initialize = null,
        Action? stop = null, string[]? dependencies = null, Func<Task>? stopAsync = null) : IPdmModule
    {
        public ModuleInfo Info { get; } = new(id, id, new Version(1, 0), DependsOn: dependencies);
        public int Starts, Stops;
        public void ConfigureServices(IServiceCollection services, HostKind host) => configure?.Invoke(services);
        public void Initialize(IModuleContext context) { Starts++; initialize?.Invoke(context); }
        public void Shutdown() { Stops++; stop?.Invoke(); }
        public async ValueTask ShutdownAsync() { Shutdown(); if (stopAsync is not null) await stopAsync(); }
    }
    private PdmHost Host(params IPdmModule[] modules) => PdmHost.Create(new()
    {
        Host = HostKind.Explorer, ModulesFolder = folder, FileLogging = false, ExtraModules = modules,
    });

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task FailedActivation_UnwindsAllCompletedStagesInReverseOrder_Once(int failedCleanup)
    {
        var owner = new ResourceLifetime(); var order = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var stage = i;
            owner.Add(async () => { await Task.Yield(); order.Add(stage); if (stage == failedCleanup) throw new IOException("cleanup"); });
        }
        var first = owner.DisposeAsync().AsTask(); Assert.Same(first, owner.DisposeAsync().AsTask());
        if (failedCleanup >= 0) await Assert.ThrowsAsync<AggregateException>(() => first); else await first;
        Assert.Equal([3,2,1,0], order);
        Assert.Throws<ObjectDisposedException>(() => owner.Add(() => Task.CompletedTask));
    }

    [Fact]
    public void FailedConfigure_RestoresAddedRemovedAndReplacedServices_AndBlocksDependents()
    {
        var original = new Marker(); Marker? observed = null; var dependentConfigured = false;
        var good = new Module("A", s => s.AddSingleton(original));
        var bad = new Module("B", s => { s.RemoveAll<Marker>(); s.AddSingleton(new Marker()); s.AddSingleton<Poison>(); throw new IOException("configure"); });
        var dependent = new Module("C", _ => dependentConfigured = true, dependencies: ["B"]);
        var independent = new Module("D", initialize: c => observed = c.Services.GetRequiredService<Marker>());
        using var host = Host(good, bad, dependent, independent); host.Start();
        Assert.Same(original, observed); Assert.Null(host.Services.GetService<Poison>()); Assert.False(dependentConfigured);
        Assert.Equal(0, bad.Stops); Assert.Equal(1, independent.Starts);
    }

    [Fact]
    public void FailedInitialize_RemovesOnlyItsUiAndSubscriptions_EvenIfShutdownThrows()
    {
        var goodEvents = 0; var badEvents = 0;
        var good = new Module("A", initialize: c =>
        {
            c.Ui.RegisterCommand(new("good", "Good", CommandPlacement.Toolbar, _ => Task.CompletedTask));
            c.Events.Subscribe<int>(_ => goodEvents++);
        });
        var bad = new Module("B", initialize: c =>
        {
            c.Services.GetRequiredService<IUiRegistry>().RegisterPanel(new("bad-panel", "Bad", PanelPlacement.Details, _ => new object()));
            c.Ui.RegisterCommand(new("bad", "Bad", CommandPlacement.Toolbar, _ => Task.CompletedTask));
            c.Services.GetRequiredService<IEventBus>().Subscribe<int>(_ => badEvents++);
            throw new IOException("initialize");
        }, stop: () => throw new IOException("shutdown"));
        var dependent = new Module("C", dependencies: ["B"]);
        var host = Host(good, bad, dependent); host.Start(); host.Start();
        Assert.Equal(["good"], host.Ui.Commands.Select(c => c.Id)); Assert.Empty(host.Ui.Panels);
        host.Events.Publish(1); Assert.Equal(1, goodEvents); Assert.Equal(0, badEvents);
        Assert.Equal(1, bad.Stops); Assert.Equal(0, dependent.Starts); Assert.Equal(1, good.Starts);
        host.Dispose(); host.Dispose(); Assert.Equal(1, good.Stops); Assert.Equal(1, bad.Stops);
    }

    [Fact]
    public async Task AsyncModuleShutdown_DelaysDisposalOfServicesUntilWorkStops()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Owned? resource = null;
        var module = new Module("A", s => s.AddSingleton<Owned>(), c => resource = c.Services.GetRequiredService<Owned>(), stopAsync: () => gate.Task);
        var host = Host(module); host.Start(); var stop = host.DisposeAsync().AsTask();
        Assert.False(stop.IsCompleted); Assert.False(resource!.Disposed);
        gate.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(resource.Disposed);
        await host.DisposeAsync(); Assert.Equal(1, module.Stops);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task FailureDuringAnyActivationStage_ReleasesPartialStageAndAllEarlierStages(int failAt)
    {
        var order = new List<int>(); var owner = new ResourceLifetime();
        var failure = Assert.Throws<IOException>(() =>
        {
            for (var i = 0; i < 6; i++)
            {
                var stage = i;
                owner.Add(() => { order.Add(stage); return Task.CompletedTask; });
                if (stage == failAt) throw new IOException("activation stage " + stage);
            }
        });
        await owner.DisposeAsync();
        Assert.Contains(failAt.ToString(), failure.Message);
        Assert.Equal(Enumerable.Range(0, failAt + 1).Reverse(), order);
    }

    [Fact]
    public async Task InFlightModuleCallback_DrainsBeforeShutdownOrServiceDisposal()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Owned? resource = null; var called = 0;
        var module = new Module("A", s => s.AddSingleton<Owned>(), c =>
        {
            resource = c.Services.GetRequiredService<Owned>();
            c.Events.Subscribe<int>(_ => { Interlocked.Increment(ref called); entered.Set(); release.Wait(); });
        });
        var host = Host(module); host.Start(); var events = host.Events;
        var publication = Task.Run(() => events.Publish(1)); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stop = host.DisposeAsync().AsTask();
        try { Assert.False(stop.IsCompleted); Assert.False(resource!.Disposed); Assert.Equal(0, module.Stops); }
        finally { release.Set(); await publication; await stop.WaitAsync(TimeSpan.FromSeconds(5)); }
        events.Publish(2); Assert.Equal(1, called); Assert.True(resource!.Disposed); Assert.Equal(1, module.Stops);
    }

    [Fact]
    public async Task StaleCommandReference_FromFailedModuleCannotExecute()
    {
        CommandDescriptor? stale = null; var executed = false;
        var module = new Module("A", initialize: c =>
        {
            c.Ui.RegisterCommand(new("partial", "Partial", CommandPlacement.Toolbar, _ => { executed = true; return Task.CompletedTask; }));
            stale = c.Ui.Commands.Single(); throw new IOException("late initialize");
        });
        using var host = Host(module); host.Start();
        Assert.Empty(host.Ui.Commands); Assert.False(stale!.IsAvailable(CommandContext.Empty));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.Execute(CommandContext.Empty)); Assert.False(executed);
    }

    private sealed class CountingLogger : ILogger
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        public int Count;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (Environment.CurrentManagedThreadId == thread) Count++; }
    }
    private static void ThrowCom() { try { throw new COMException("test COM"); } catch (COMException) { } }
    [Fact]
    public void GlobalDiagnostics_AreDetachedAcrossRepeatedActivationCycles()
    {
        var previous = new List<(CountingLogger Logger,int Count)>();
        for (var i = 0; i < 3; i++)
        {
            var logger = new CountingLogger(); var monitor = new ErrorMonitor(logger);
            ThrowCom(); Assert.True(logger.Count > 0);
            monitor.Dispose(); monitor.Dispose(); var count = logger.Count;
            ThrowCom(); Assert.Equal(count, logger.Count); previous.Add((logger, count));
        }
        foreach (var (logger, count) in previous) Assert.Equal(count, logger.Count);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference DisposedMonitorLogger()
    {
        var logger = new CountingLogger(); var monitor = new ErrorMonitor(logger); monitor.Dispose();
        return new WeakReference(logger);
    }
    [Fact]
    public void DisposedGlobalSubscriptions_DoNotKeepTheirLoggerAlive()
    {
        var logger = DisposedMonitorLogger();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            return !logger.IsAlive;
        }, TimeSpan.FromSeconds(3)));
    }
    public void Dispose() => Directory.Delete(folder, true);
}
