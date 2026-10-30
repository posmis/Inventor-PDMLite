using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;
using PdmLite.Link;

namespace PdmLite.AddIn;

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

    public InventorEventsBridge(IServiceProvider services, InventorLinkServer link, ILogger logger)
    {
        _inventor = services.GetRequiredService<Inv.Application>();
        _events = services.GetRequiredService<IEventBus>();
        _session = services.GetRequiredService<IProjectSession>();
        _projects = services.GetRequiredService<InventorProjects>();
        _link = link;
        _logger = logger;

        _appEvents = _inventor.ApplicationEvents;
        _appEvents.OnActivateDocument += OnActivateDocument;
        _appEvents.OnSaveDocument += OnSaveDocument;
        _appEvents.OnCloseDocument += OnCloseDocument;
    }

    /// <summary>Открывает в дереве активный проект Inventor, если он сменился.</summary>
    public async Task SyncActiveProjectAsync()
    {
        try
        {
            var project = _projects.GetActive();
            if (project is null || IsSameProject(project, _session.Project))
                return;
            await _session.OpenAsync(project);
        }
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
        if (beforeOrAfter != Inv.EventTimingEnum.kAfter || PathOf(documentObject) is not { } path)
            return;

        _events.Publish(new DocumentSaved(path));
        _link.Broadcast(LinkProtocol.Events.DocumentSaved, path);
    }

    private void OnCloseDocument(Inv._Document documentObject, string fullDocumentName, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out Inv.HandlingCodeEnum handlingCode)
    {
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
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

    public void Dispose()
    {
        _appEvents.OnActivateDocument -= OnActivateDocument;
        _appEvents.OnSaveDocument -= OnSaveDocument;
        _appEvents.OnCloseDocument -= OnCloseDocument;
    }
}
