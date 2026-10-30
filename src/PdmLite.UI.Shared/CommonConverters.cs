using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PdmLite.UI.Shared;

/// <summary>Пустая строка или <c>null</c> → Collapsed, иначе Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static NullToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Логическое «не».</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static InverseBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary><c>true</c> → Collapsed, иначе Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static InverseBoolToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
