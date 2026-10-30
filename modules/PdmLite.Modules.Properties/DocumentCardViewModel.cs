using CommunityToolkit.Mvvm.ComponentModel;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Threading;

namespace PdmLite.Modules.Properties;

/// <summary>
/// Карточка выбранного документа. На этом этапе — сведения о файле;
/// iProperties (обозначение, наименование) добавятся вместе с адаптерами Inventor/Apprentice.
/// </summary>
public sealed partial class DocumentCardViewModel(IEventBus events, IUiDispatcher ui) : ObservableObject
{
    private IDisposable? _subscription;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem), nameof(Name), nameof(Kind), nameof(KindName), nameof(FullPath), nameof(Folder), nameof(SizeText), nameof(ModifiedText))]
    private ProjectItem? _item;

    public bool HasItem => Item is not null;
    public string Name => Item?.Name ?? "";
    public DocumentKind Kind => Item?.Kind ?? DocumentKind.Other;
    public string KindName => Item is null ? "" : DocumentKinds.DisplayName(Item.Kind);
    public string FullPath => Item?.Path ?? "";
    public string Folder => Item is null ? "" : System.IO.Path.GetDirectoryName(Item.Path) ?? "";
    public string SizeText => Item is { IsFolder: false } i ? FormatSize(i.Size) : "";
    public string ModifiedText => Item is null || Item.ModifiedUtc == DateTime.MinValue ? "" : Item.ModifiedUtc.ToLocalTime().ToString("g");

    internal void Attach() =>
        _subscription = events.Subscribe<SelectionChanged>(e => ui.Post(() => Item = e.Items.Count == 1 ? e.Items[0] : null));

    internal void Detach() => _subscription?.Dispose();

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / (1024.0 * 1024):0.##} МБ",
    };
}
