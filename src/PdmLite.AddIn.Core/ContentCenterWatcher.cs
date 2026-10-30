using PdmLite.Core.Events;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Следит за вставкой компонентов в сборки и сообщает (событием шины), если компонент взят
/// из Content Center. Что с этим делать, решает модуль библиотеки.
/// </summary>
public sealed class ContentCenterWatcher : IDisposable
{
    private readonly Inv.AssemblyEvents _assemblyEvents; // держим ссылку, иначе COM-подписка может быть собрана GC
    private readonly IEventBus _events;

    public ContentCenterWatcher(Inv.Application inventor, IEventBus events)
    {
        _events = events;
        _assemblyEvents = inventor.AssemblyEvents;
        _assemblyEvents.OnNewOccurrence += OnNewOccurrence;
    }

    private void OnNewOccurrence(Inv.AssemblyDocument documentObject, Inv.ComponentOccurrence occurrence, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out Inv.HandlingCodeEnum handlingCode)
    {
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter != Inv.EventTimingEnum.kAfter || !InventorAssemblyComponents.IsContentCenter(occurrence))
            return;

        var path = occurrence.ReferencedDocumentDescriptor?.FullDocumentName;
        if (string.IsNullOrEmpty(path))
            return;
        _events.Publish(new ContentCenterComponentPlaced(documentObject.FullFileName, path,
            InventorProperties.PartNumber(occurrence.Definition?.Document as Inv._Document)));
    }

    public void Dispose() => _assemblyEvents.OnNewOccurrence -= OnNewOccurrence;
}
