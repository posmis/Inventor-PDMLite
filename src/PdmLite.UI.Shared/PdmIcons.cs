using System.Windows;
using System.Windows.Media;

namespace PdmLite.UI.Shared;

/// <summary>Доступ к иконкам темы из кода (ключи — <see cref="Core.UI.IconKeys"/>).</summary>
public static class PdmIcons
{
    private static readonly Lazy<ResourceDictionary> Icons = new(() => new ResourceDictionary
    {
        Source = new Uri("/PdmLite.UI.Shared;component/Themes/Icons.xaml", UriKind.Relative),
    });

    /// <summary>Иконка по ключу или <c>null</c>, если ключ пустой или неизвестен.</summary>
    public static ImageSource? Get(string? key) =>
        key is not null && Icons.Value.Contains("Pdm.Icon." + key) ? (ImageSource)Icons.Value["Pdm.Icon." + key] : null;

    /// <summary>Готовый элемент Image 16×16 (для меню и кнопок).</summary>
    public static FrameworkElement? Image(string? key, double size = 16) =>
        Get(key) is { } source
            ? new System.Windows.Controls.Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform }
            : null;
}
