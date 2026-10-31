using System.Windows.Controls;
using System.Windows.Input;

namespace PdmLite.Modules.References;

public partial class ReferencesPanelView : UserControl
{
    public ReferencesPanelView()
    {
        InitializeComponent();
    }

    private ReferencesPanelViewModel? ViewModel => DataContext as ReferencesPanelViewModel;

    /// <summary>Выделение — одно на оба списка: выбрали в одном, в другом снимается.</summary>
    private void List_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not ReferenceRow row || ViewModel is not { } vm)
            return;
        var other = ReferenceEquals(list, UsedInList) ? ComponentsList : UsedInList;
        other.SelectedItem = null;
        vm.Selected = row;
    }

    private void List_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.OpenCommand is { } open && open.CanExecute(null))
            open.Execute(null);
    }
}
