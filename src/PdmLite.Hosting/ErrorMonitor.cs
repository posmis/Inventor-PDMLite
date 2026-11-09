using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace PdmLite.Hosting;

/// <summary>Global diagnostics owned by one activation, not by the process lifetime.</summary>
public sealed class ErrorMonitor : IDisposable
{
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, bool> logged = new();
    private bool disposed;
    [ThreadStatic] private static bool reporting;
    public ErrorMonitor(ILogger logger)
    {
        this.logger = logger;
        AppDomain.CurrentDomain.FirstChanceException += FirstChance;
        AppDomain.CurrentDomain.UnhandledException += Unhandled;
        TaskScheduler.UnobservedTaskException += Unobserved;
    }
    private void Report(Action action)
    {
        if (reporting) return;
        lock (gate)
        {
            if (disposed) return;
            reporting = true;
            try { action(); } catch { /* Diagnostics must not throw into a runtime exception event. */ }
            finally { reporting = false; }
        }
    }
    private void FirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is not COMException com || reporting) return;
        Report(() =>
        {
            var stack = com.StackTrace ?? new System.Diagnostics.StackTrace(com, false).ToString();
            if (!stack.Contains("PdmLite", StringComparison.Ordinal) || logged.Count >= 500 || !logged.TryAdd(com.HResult + stack, true)) return;
            logger.LogWarning("Ошибка Inventor API (0x{Code:X8}): {Message}\n{Stack}", com.HResult, com.Message, stack);
        });
    }
    private void Unhandled(object sender, UnhandledExceptionEventArgs e) =>
        Report(() => logger.LogCritical(e.ExceptionObject as Exception, "Необработанная ошибка (завершение: {Terminating})", e.IsTerminating));
    private void Unobserved(object? sender, UnobservedTaskExceptionEventArgs e) =>
        Report(() => { logger.LogError(e.Exception, "Необработанная ошибка фоновой задачи"); e.SetObserved(); });
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            AppDomain.CurrentDomain.FirstChanceException -= FirstChance;
            AppDomain.CurrentDomain.UnhandledException -= Unhandled;
            TaskScheduler.UnobservedTaskException -= Unobserved;
            logged.Clear();
        }
    }
}
