using System.Windows;

namespace PdmLite.UI.Shared;

public static class ThemeResources
{
    public static readonly Uri ThemeUri = new("/PdmLite.UI.Shared;component/Themes/Theme.xaml", UriKind.Relative);

    /// <summary>
    /// Тема PdmLite. Корневые элементы без WPF-приложения (окно плагина в Inventor)
    /// подключают её в свои ресурсы сами.
    /// </summary>
    public static ResourceDictionary Create() => new() { Source = ThemeUri };
}
