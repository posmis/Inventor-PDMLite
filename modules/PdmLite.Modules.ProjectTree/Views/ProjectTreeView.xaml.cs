using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            if (item.DataContext is TreeNodeViewModel node)
                ViewModel?.RightClick(node);
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

    // --- Перетаскивание: файл или папку — на папку (перемещение с переносом ссылок) ---

    private const string DragFormat = "PdmLite.ProjectItem";
    private Point _dragStart;
    private TreeNodeViewModel? _dragNode;

    private void Tree_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        // Перетаскивать можно за строку узла, но не за полосу прокрутки.
        _dragNode = e.OriginalSource is DependencyObject source && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is null
            ? FindNode(source)
            : null;

        // Выделение нескольких: Ctrl — добавить/убрать, Shift — диапазон. Щелчок по стрелке раскрытия выделение не меняет.
        if (_dragNode is null || ViewModel is not { } vm || FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) is not null)
            return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!ctrl && !shift && _dragNode.IsMarked && vm.Marked.Count > 1)
            return; // щелчок по уже выделенному — может начаться перетаскивание всех выделенных
        vm.Click(_dragNode, ctrl, shift);
        if (ctrl || shift)
        {
            e.Handled = true; // выделенный (текущий) элемент не меняем
            Tree.Focus();
        }
    }

    private void Tree_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Щелчок без перетаскивания по одному из нескольких выделенных — теперь выделен только он.
        if (ViewModel is { } vm && _dragNode is { IsMarked: true } node && Keyboard.Modifiers == ModifierKeys.None && vm.Marked.Count > 1)
            vm.Click(node, ctrl: false, shift: false);
    }

    private void Tree_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragNode is not { Parent: not null } node)
            return;
        var delta = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragNode = null;
        var paths = node.IsMarked && ViewModel is { } vm && vm.Marked.Count > 1
            ? vm.Marked.Select(n => n.Item.Path).ToArray()
            : [node.Item.Path];
        DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, string.Join("\n", paths)), DragDropEffects.Move);
    }

    private void Tree_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropTarget(e) is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tree_OnDrop(object sender, DragEventArgs e)
    {
        if (DropTarget(e) is not { } target || e.Data.GetData(DragFormat) is not string data)
            return;
        e.Handled = true;
        ViewModel?.RequestMove(data.Split('\n'), target.Item.Path);
    }

    /// <summary>Папка под курсором, куда можно перенести перетаскиваемое (не в себя и не туда, где оно уже лежит).</summary>
    private TreeNodeViewModel? DropTarget(DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string data || (e.OriginalSource as DependencyObject) is not { } source)
            return null;
        var paths = data.Split('\n');
        var node = FindNode(source);
        if (node is null)
            return null;
        var folder = node.IsFolder ? node : node.Parent;
        if (folder is null)
            return null;
        var target = folder.Item.Path;
        // Не в саму себя (и не в свою вложенную) и не туда, где всё уже лежит.
        if (paths.Any(p => (target + Path.DirectorySeparatorChar).StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || paths.All(p => string.Equals(Path.GetDirectoryName(p), target, StringComparison.OrdinalIgnoreCase)))
            return null;
        return folder;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null && element is not T)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    private static TreeNodeViewModel? FindNode(DependencyObject? element)
    {
        return FindAncestor<TreeViewItem>(element)?.DataContext as TreeNodeViewModel;
    }

    private sealed class RelayKeyCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
