using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;
using PdmLite.Link;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Переводит события Inventor в события шины PdmLite и рассылает их в Explorer.
/// Также следит за сменой активного проекта Inventor.
/// </summary>
public sealed class InventorEventsBridge : IDisposable
{
    private readonly Inv.Application _inventor;
    private readonly Inv.ApplicationEvents _appEvents; // держим ссылку, иначе COM-подписка может быть собрана GC
    private readonly IEventBus _events;
    private readonly IProjectSession _session;
    private readonly InventorProjects _projects;
    private readonly InventorLinkServer _link;
    private readonly ILogger _logger;
    private readonly PdmLite.Core.Lifetime.ResourceLifetime _subscriptions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _sync = [];
    private Task? _stopping;

    public InventorEventsBridge(IServiceProvider services, InventorLinkServer link, ILogger logger)
    {
        _inventor = services.GetRequiredService<Inv.Application>();
        _events = services.GetRequiredService<IEventBus>();
        _session = services.GetRequiredService<IProjectSession>();
        _projects = services.GetRequiredService<InventorProjects>();
        _link = link;
        _logger = logger;

        _appEvents = _inventor.ApplicationEvents;
    }

    public void Attach()
    {
        _subscriptions.Add(() => { _appEvents.OnActivateDocument -= OnActivateDocument; return Task.CompletedTask; });
        _appEvents.OnActivateDocument += OnActivateDocument;
        _subscriptions.Add(() => { _appEvents.OnSaveDocument -= OnSaveDocument; return Task.CompletedTask; });
        _appEvents.OnSaveDocument += OnSaveDocument;
        _subscriptions.Add(() => { _appEvents.OnCloseDocument -= OnCloseDocument; return Task.CompletedTask; });
        _appEvents.OnCloseDocument += OnCloseDocument;
    }

    /// <summary>Открывает в дереве активный проект Inventor, если он сменился.</summary>
    public Task SyncActiveProjectAsync()
    {
        lock (_gate)
        {
            if (_stopping is not null) return Task.CompletedTask;
            var task = SyncProjectCoreAsync(); _sync.Add(task);
            _ = task.ContinueWith(done => { lock (_gate) _sync.Remove(done); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task SyncProjectCoreAsync()
    {
        try
        {
            var project = _projects.GetActive();
            if (project is null || IsSameProject(project, _session.Project))
                return;
            await _session.OpenAsync(project, _stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось открыть активный проект Inventor");
        }
    }

    private static bool IsSameProject(ProjectInfo a, ProjectInfo? b) =>
        b is not null
        && string.Equals(a.RootFolder, b.RootFolder, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ProjectFile, b.ProjectFile, StringComparison.OrdinalIgnoreCase);

    private void OnActivateDocument(Inv._Document documentObject, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out Inv.HandlingCodeEnum handlingCode)
    {
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
        if (_stopping is not null) return;
        if (beforeOrAfter != Inv.EventTimingEnum.kAfter)
            return;

        var path = PathOf(documentObject);
        _events.Publish(new ActiveDocumentChanged(path));
        _link.Broadcast(LinkProtocol.Events.ActiveDocumentChanged, path);

        // В Inventor нет события смены проекта; проверяем при активации документа.
        _ = SyncActiveProjectAsync();
    }

    private void OnSaveDocument(Inv._Document documentObject, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out Inv.HandlingCodeEnum handlingCode)
    {
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
        if (_stopping is not null) return;
        if (beforeOrAfter != Inv.EventTimingEnum.kAfter || PathOf(documentObject) is not { } path)
            return;

        _events.Publish(new DocumentSaved(path));
        _link.Broadcast(LinkProtocol.Events.DocumentSaved, path);
    }

    private void OnCloseDocument(Inv._Document documentObject, string fullDocumentName, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out Inv.HandlingCodeEnum handlingCode)
    {
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
        if (_stopping is not null) return;
        if (beforeOrAfter != Inv.EventTimingEnum.kAfter)
            return;

        if (!string.IsNullOrEmpty(fullDocumentName))
        {
            _events.Publish(new DocumentClosed(fullDocumentName));
            _link.Broadcast(LinkProtocol.Events.DocumentClosed, fullDocumentName);
        }

        // Закрыт последний документ — активного больше нет.
        if (_inventor.ActiveDocument is null)
        {
            _events.Publish(new ActiveDocumentChanged(null));
            _link.Broadcast(LinkProtocol.Events.ActiveDocumentChanged, null);
        }
    }

    private static string? PathOf(Inv._Document document)
    {
        var path = document.FullFileName;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopping is not null) return _stopping;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = done.Task;
            _stop.Cancel();
            _ = StopCoreAsync(_sync.ToArray(), done);
            return _stopping;
        }
    }
    private async Task StopCoreAsync(Task[] work, TaskCompletionSource done)
    {
        var errors = new List<Exception>();
        try { await _subscriptions.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        try { await Task.WhenAll(work); } catch (Exception ex) { errors.Add(ex); }
        _stop.Dispose();
        if (errors.Count == 0) done.TrySetResult(); else done.TrySetException(new AggregateException(errors));
    }
    public void Dispose() => _ = StopAsync();
}
