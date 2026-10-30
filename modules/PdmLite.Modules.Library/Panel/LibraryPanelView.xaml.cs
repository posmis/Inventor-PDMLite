using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PdmLite.Modules.Library.Panel;

public partial class LibraryPanelView : UserControl
{
    public LibraryPanelView()
    {
        InitializeComponent();
        // Каталог читается из базы при первом показе вкладки, а не при запуске программы.
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && ViewModel is { } vm)
                await vm.EnsureLoadedAsync();
        };
    }

    private LibraryPanelViewModel? ViewModel => DataContext as LibraryPanelViewModel;

    private void Tree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } vm)
            vm.Selected = e.NewValue as LibraryNode;
    }

    private void TreeItem_OnSelected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is TreeViewItem item)
            item.BringIntoView();
    }

    private void TreeItem_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Событие всплывает по родительским узлам — реагируем только на выделенный компонент.
        if (sender is not TreeViewItem { IsSelected: true, DataContext: LibraryNode { IsFolder: false } })
            return;
        e.Handled = true;
        if (ViewModel?.PlaceCommand is { } place && place.CanExecute(null))
            place.Execute(null);
    }
}
