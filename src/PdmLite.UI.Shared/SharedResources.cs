using System.Windows;

namespace PdmLite.UI.Shared;

public static class SharedResources
{
    private static readonly Uri ThemeUri = new("pack://application:,,,/PdmLite.UI.Shared;component/Themes/Shared.xaml", UriKind.Absolute);

    /// <summary>
    /// Общие стили. Панели подключают их в своих ресурсах — так они работают и внутри
    /// Inventor, где нет WPF-объекта <see cref="Application"/> с общими ресурсами.
    /// </summary>
    public static ResourceDictionary Create() => new() { Source = ThemeUri };
}
