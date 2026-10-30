using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PdmLite.Core.Naming;

namespace PdmLite.UI.Shared;

/// <summary>
/// Сортировка колонок таблицы в «естественном» порядке: числа по величине
/// (M6, M8, M12; длина 20 раньше 100). Включается: <c>ui:NaturalSort.IsEnabled="True"</c>.
/// </summary>
public static class NaturalSort
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(NaturalSort), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
            return;
        grid.Sorting -= OnSorting;
        if (e.NewValue is true)
            grid.Sorting += OnSorting;
    }

    private static void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        var grid = (DataGrid)sender;
        var path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path) || CollectionViewSource.GetDefaultView(grid.ItemsSource) is not ListCollectionView view)
            return; // обычная сортировка

        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in grid.Columns)
            if (column != e.Column)
                column.SortDirection = null;
        e.Column.SortDirection = direction;
        view.CustomSort = new MemberComparer(path, direction);
    }

    /// <summary>Сравнение строк по значению свойства (путь вида «Item.Designation»).</summary>
    private sealed class MemberComparer(string path, ListSortDirection direction) : IComparer
    {
        private readonly string[] _parts = path.Split('.');
        private readonly Dictionary<(Type, string), PropertyInfo?> _properties = [];

        public int Compare(object? x, object? y)
        {
            var a = Value(x);
            var b = Value(y);
            var result = a is string || b is string
                ? NaturalComparer.Instance.Compare(a?.ToString(), b?.ToString())
                : Comparer.Default.Compare(a, b);
            return direction == ListSortDirection.Ascending ? result : -result;
        }

        private object? Value(object? item)
        {
            foreach (var part in _parts)
            {
                if (item is null)
                    return null;
                var key = (item.GetType(), part);
                if (!_properties.TryGetValue(key, out var property))
                    _properties[key] = property = key.Item1.GetProperty(part);
                item = property?.GetValue(item);
            }
            return item;
        }
    }
}
