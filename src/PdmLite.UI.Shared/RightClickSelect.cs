using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PdmLite.UI.Shared;

/// <summary>
/// Правый щелчок выделяет строку таблицы или узел дерева под курсором, чтобы контекстное меню
/// относилось к нему. Щелчок по уже выделенной строке сохраняет выделение (команда — для всех
/// выделенных). Включается: <c>ui:RightClickSelect.IsEnabled="True"</c> на DataGrid или TreeView.
/// </summary>
public static class RightClickSelect
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(RightClickSelect), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;
        element.PreviewMouseRightButtonDown -= OnRightButtonDown;
        if (e.NewValue is true)
            element.PreviewMouseRightButtonDown += OnRightButtonDown;
    }

    private static void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        switch (sender)
        {
            case DataGrid grid when FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { } row:
                if (!row.IsSelected)
                {
                    if (grid.SelectionMode == DataGridSelectionMode.Extended)
                        grid.SelectedItems.Clear();
                    row.IsSelected = true;
                }
                row.Focus();
                break;
            case TreeView when FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item:
                item.IsSelected = true;
                item.Focus();
                break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}

/// <summary>Буфер обмена: занят другой программой — не ошибка, просто не скопировалось.</summary>
public static class UiClipboard
{
    public static void SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    /// <summary>Показать файл в проводнике Windows (с выделением).</summary>
    public static void ShowInExplorer(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            UiMessages.Error($"Файл не найден:\n{path}");
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }
}

/// <summary>
/// Дерево не прокручивается само вправо при выделении узла. WPF при выделении (и при раскрытии длинного узла)
/// прокручивает так, чтобы был виден весь узел вместе с вложенными, — и горизонтальная прокрутка «уезжает».
/// Здесь по вертикали прокрутка остаётся как в WPF (узел виден), а по горизонтали — где её оставил пользователь.
/// Включается для всех деревьев стилем темы.
/// </summary>
public static class TreeScroll
{
    public static readonly DependencyProperty KeepHorizontalProperty = DependencyProperty.RegisterAttached(
        "KeepHorizontal", typeof(bool), typeof(TreeScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetKeepHorizontal(DependencyObject d) => (bool)d.GetValue(KeepHorizontalProperty);
    public static void SetKeepHorizontal(DependencyObject d, bool value) => d.SetValue(KeepHorizontalProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView tree)
            return;
        tree.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, (RequestBringIntoViewEventHandler)OnRequestBringIntoView);
        if (e.NewValue is true)
            tree.AddHandler(FrameworkElement.RequestBringIntoViewEvent, (RequestBringIntoViewEventHandler)OnRequestBringIntoView, handledEventsToo: true);
    }

    private static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is not TreeView tree || FindScrollViewer(tree) is not { } scroll)
            return;
        var offset = scroll.HorizontalOffset;
        // Вертикальную прокрутку WPF выполнит сам; горизонтальную после неё возвращаем.
        tree.Dispatcher.BeginInvoke(() => scroll.ScrollToHorizontalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer scroll)
                return scroll;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }
        return null;
    }
}
