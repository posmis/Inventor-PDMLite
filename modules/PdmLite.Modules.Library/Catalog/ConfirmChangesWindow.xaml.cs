using System.Windows;

namespace PdmLite.Modules.Library.Catalog;

/// <summary>Строка списка изменений: компонент, что меняется, было (красным), станет (зелёным).</summary>
public sealed record ChangeLine(string A, string B, string C, string D);

/// <summary>
/// Последнее предупреждение перед изменением файлов: полный список того, что будет сделано.
/// По умолчанию выбрана «Отмена».
/// </summary>
public partial class ConfirmChangesWindow : Window
{
    public ConfirmChangesWindow(string title, string header, string totals, string notes, string okText,
        (string A, string B, string C, string D) columns, IReadOnlyList<ChangeLine> lines)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        TotalsText.Text = totals;
        NotesText.Text = notes;
        OkButton.Content = okText;
        (ColA.Header, ColB.Header, ColC.Header, ColD.Header) = columns;
        Grid.ItemsSource = lines;
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
