using System.Globalization;
using System.Windows.Data;
using PdmLite.Core.Domain;
using PdmLite.Core.UI;

namespace PdmLite.UI.Shared;

/// <summary>
/// Иконка элемента проекта: папки и документы Inventor — иконки темы PdmLite, остальные файлы — иконка типа
/// из Windows (как в Проводнике).
/// </summary>
/// <remarks>
/// Значение: <see cref="ProjectItem"/>, путь к файлу (string) или <see cref="DocumentKind"/>.
/// Параметр <c>Large</c> — иконка 32×32.
/// </remarks>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var large = parameter is string p && p.Equals("Large", StringComparison.OrdinalIgnoreCase);
        return value switch
        {
            ProjectItem { IsFolder: true } => PdmIcons.Get(IconKeys.Folder),
            ProjectItem item => ForFile(item.Path, item.Kind, large),
            string { Length: > 0 } path => ForFile(path, DocumentKinds.FromPath(path), large),
            DocumentKind kind => PdmIcons.Get(IconKeys.ForKind(kind)),
            _ => null,
        };
    }

    /// <summary>
    /// Документы Inventor (.ipt, .iam, .idw, .dwg, .ipn, .ipj) — иконками PdmLite в стиле Inventor 2025: Windows
    /// показывает для них значки той версии Inventor, что зарегистрировала расширения (часто старые). Остальное
    /// (PDF, STEP…) — как в проводнике Windows.
    /// </summary>
    private static object? ForFile(string path, DocumentKind kind, bool large) =>
        kind != DocumentKind.Other
            ? PdmIcons.Get(IconKeys.ForKind(kind))
            : ShellIcons.ForExtension(System.IO.Path.GetExtension(path), large) ?? PdmIcons.Get(IconKeys.ForKind(kind));

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
