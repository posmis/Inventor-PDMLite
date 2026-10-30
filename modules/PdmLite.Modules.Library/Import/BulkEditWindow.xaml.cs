using System.Text.RegularExpressions;
using System.Windows;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Import;

/// <summary>Правка выделенных строк разом: тип, производитель, раздел, замена в обозначении.</summary>
public partial class BulkEditWindow : Window
{
    private readonly ImportViewModel _viewModel;
    private readonly IReadOnlyList<ImportRowViewModel> _rows;

    public BulkEditWindow(ImportViewModel viewModel, IReadOnlyList<ImportRowViewModel> rows)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _rows = rows;
        CountText.Text = $"Выделено строк: {rows.Count}.";
        KindBox.ItemsSource = ImportRowViewModel.KindNames;
        KindBox.SelectedItem = rows[0].KindName;
        CategoryBox.ItemsSource = viewModel.Categories;
        CategoryBox.SelectedItem = rows[0].Category;
        ManufacturerBox.Text = rows[0].Manufacturer ?? "";
        UpdatePreview();
    }

    private void Replace_OnChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    /// <summary>Пример замены на первой подходящей строке — до применения.</summary>
    private void UpdatePreview()
    {
        if (PreviewText is null)
            return;
        if (FindBox.Text.Length == 0)
        {
            PreviewText.Text = "";
            return;
        }

        try
        {
            var regex = RegexCheck.IsChecked == true
                ? new Regex(FindBox.Text, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))
                : null;
            var hits = 0;
            string? example = null;
            foreach (var row in _rows)
            {
                var next = regex is null
                    ? row.Designation.Replace(FindBox.Text, ReplaceBox.Text, StringComparison.Ordinal)
                    : regex.Replace(row.Designation, ReplaceBox.Text);
                if (next == row.Designation)
                    continue;
                hits++;
                example ??= $"«{row.Designation}» → «{next}»";
            }
            PreviewText.Text = hits == 0 ? "Совпадений нет." : $"Изменится строк: {hits}. Например: {example}";
        }
        catch (ArgumentException ex)
        {
            PreviewText.Text = "Ошибка в выражении: " + ex.Message;
        }
        catch (RegexMatchTimeoutException)
        {
            PreviewText.Text = "Выражение выполняется слишком долго.";
        }
    }

    private void Apply_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FindBox.Text.Length > 0)
                _viewModel.ReplaceInDesignations(_rows, FindBox.Text, ReplaceBox.Text, RegexCheck.IsChecked == true);

            LibraryItemKind? kind = KindCheck.IsChecked == true
                ? KindBox.SelectedItem as string == ImportRowViewModel.KindNames[1] ? LibraryItemKind.Purchased : LibraryItemKind.Standard
                : null;
            var manufacturer = ManufacturerCheck.IsChecked == true ? ManufacturerBox.Text : null;
            var category = CategoryCheck.IsChecked == true ? CategoryBox.SelectedItem as CategoryInfo : null;
            if (kind is not null || manufacturer is not null || category is not null)
                _viewModel.BulkEdit(_rows, kind, manufacturer, category);
        }
        catch (ArgumentException ex)
        {
            UiMessages.Error("Ошибка в выражении замены.", ex);
            return;
        }

        DialogResult = true;
    }
}
