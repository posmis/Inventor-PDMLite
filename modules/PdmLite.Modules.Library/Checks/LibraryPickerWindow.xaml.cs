using System.Windows;
using System.Windows.Input;
using PdmLite.Core.Naming;
using PdmLite.Data.Library;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;

namespace PdmLite.Modules.Library.Checks;

/// <summary>
/// Ручной выбор компонента библиотеки (например, для замены в сборке, когда похожий не нашёлся).
/// Поиск — по ключу обозначения: «м8 х30» найдёт «M8-6gx30»; слова можно писать в любом порядке.
/// </summary>
public partial class LibraryPickerWindow : Window
{
    private readonly IReadOnlyList<PickerRow> _all;

    public LibraryPickerWindow(IEnumerable<LibraryItemSummary> library, string forDesignation, string initialSearch)
    {
        InitializeComponent();
        _all = library.Select(i => new PickerRow(i)).OrderBy(r => r.Designation, NaturalComparer.Instance).ToList();
        ForText.Text = $"Замена для «{forDesignation}»:";
        SearchBox.Text = initialSearch;
        Apply();
        Loaded += (_, _) => SearchBox.Focus();
    }

    public LibraryItemSummary? Selected { get; private set; }

    private void Search_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Apply();

    private void Apply()
    {
        if (Grid is null)
            return;
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(DesignationNormalizer.ToKey).ToList();
        var shown = _all.Where(r => words.All(w => r.Key.Contains(w, StringComparison.Ordinal))).ToList();
        Grid.ItemsSource = shown;
        CountText.Text = $"Показано: {shown.Count} из {_all.Count}";
        if (shown.Count == 1)
            Grid.SelectedIndex = 0;
    }

    private void Grid_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OkButton.IsEnabled = Grid.SelectedItem is PickerRow;

    private void Grid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is PickerRow)
            Ok_OnClick(sender, e);
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not PickerRow row)
            return;
        Selected = row.Item;
        DialogResult = true;
    }

    private sealed record PickerRow(LibraryItemSummary Item)
    {
        public string Designation => Item.Designation;
        public string CategoryPath => Item.CategoryPath;
        public string Detail => Item.Kind == LibraryItemKind.Standard ? Item.Standard ?? "" : Item.Manufacturer ?? "";

        /// <summary>Ключ для поиска: обозначение и раздел.</summary>
        public string Key { get; } = DesignationNormalizer.ToKey(Item.Designation + " " + Item.CategoryPath);
    }
}
