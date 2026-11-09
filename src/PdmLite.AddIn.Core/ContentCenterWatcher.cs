using PdmLite.Core.Events;
using PdmLite.Core.Documents;
using Microsoft.Extensions.Logging;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Следит за вставкой компонентов в сборки и сообщает (событием шины), если компонент взят
/// из Content Center. Что с этим делать, решает модуль библиотеки.
/// </summary>
public sealed class ContentCenterWatcher : IDisposable
{
    private readonly Inv.AssemblyEvents _assemblyEvents; // держим ссылку, иначе COM-подписка может быть собрана GC
    private readonly IEventBus _events;
    private readonly ILogger _logger;

    public ContentCenterWatcher(Inv.Application inventor, IEventBus events, ILogger logger)
    {
        _events = events;
        _logger = logger;
        _assemblyEvents = inventor.AssemblyEvents;
        _assemblyEvents.OnNewOccurrence += OnNewOccurrence;
    }

    private void OnNewOccurrence(Inv.AssemblyDocument documentObject, Inv.ComponentOccurrence occurrence, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out Inv.HandlingCodeEnum handlingCode)
    {
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter != Inv.EventTimingEnum.kAfter) return;
        ContentCenterNotification.PublishSafely(
            () => InventorAssemblyComponents.IsContentCenter(occurrence),
            () => documentObject.FullFileName,
            () => occurrence.ReferencedDocumentDescriptor?.FullDocumentName,
            () => InventorProperties.PartNumber(occurrence.Definition?.Document as Inv._Document),
            _events.Publish, _logger);
    }

    public void Dispose() => _assemblyEvents.OnNewOccurrence -= OnNewOccurrence;
}
