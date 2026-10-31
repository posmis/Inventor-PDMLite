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
