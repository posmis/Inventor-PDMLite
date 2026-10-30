using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdmLite.Core.UI;
using PdmLite.Modules.ProjectTree.ViewModels;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.ProjectTree.Views;

public partial class ProjectTreeView : UserControl
{
    public ProjectTreeView()
    {
        InitializeComponent();
        InputBindings.Add(new KeyBinding(new RelayKeyCommand(() => ViewModel?.RefreshCommand.Execute(null)), Key.F5, ModifierKeys.None));
    }

    private ProjectTreeViewModel? ViewModel => DataContext as ProjectTreeViewModel;

    private void Tree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } vm)
            vm.SelectedNode = e.NewValue as TreeNodeViewModel;
    }

    private void TreeItem_OnSelected(object sender, RoutedEventArgs e)
    {
        // Выделение из кода (например, документ активирован в Inventor) — прокручиваем к узлу.
        if (ReferenceEquals(sender, e.OriginalSource) && sender is TreeViewItem item)
            item.BringIntoView();
    }

    private async void TreeItem_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Событие всплывает по всем родительским узлам — обрабатываем только выделенный.
        if (sender is not TreeViewItem { IsSelected: true, DataContext: TreeNodeViewModel { IsFolder: false } })
            return;
        e.Handled = true;
        if (ViewModel is { } vm)
            await vm.ExecuteDefaultAsync();
    }

    private void TreeItem_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Правый щелчок выделяет узел, чтобы контекстное меню относилось к нему.
        if (sender is TreeViewItem item && ReferenceEquals(ItemsControl.ContainerFromElement(item, (DependencyObject)e.OriginalSource), null))
        {
            item.IsSelected = true;
            item.Focus();
            e.Handled = true;
        }
    }

    private void Tree_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ViewModel is not { SelectedNode: not null } vm)
        {
            e.Handled = true;
            return;
        }

        var menu = Tree.ContextMenu!;
        menu.Items.Clear();
        foreach (var item in CommandMenuBuilder.Build(vm.Registry, CommandPlacement.ContextMenu, vm.CurrentContext))
            menu.Items.Add(item);

        if (menu.Items.Count == 0)
            e.Handled = true;
    }

    private sealed class RelayKeyCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
