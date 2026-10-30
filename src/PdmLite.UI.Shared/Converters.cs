using System.Globalization;
using System.Windows.Data;
using PdmLite.Core.Domain;
using PdmLite.Core.UI;

namespace PdmLite.UI.Shared;

/// <summary><see cref="DocumentKind"/> → иконка 16×16.</summary>
public sealed class DocumentKindToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DocumentKind kind ? PdmIcons.Get(IconKeys.ForKind(kind)) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Ключ иконки (строка из <see cref="IconKeys"/>) → иконка.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        PdmIcons.Get(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
