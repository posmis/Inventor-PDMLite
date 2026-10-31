using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PdmLite.UI.Shared;

/// <summary>
/// Панель инструментов как в Inventor: кнопки в ряд; если места не хватает, у кнопок со значком
/// справа налево скрывается текст (остаются значок и подсказка), и только когда текст скрыт у всех —
/// панель обрезается. Кнопки без значка текст не теряют.
/// </summary>
public sealed class ToolbarPanel : Panel
{
    /// <summary>Ширина подписи кнопки, когда она видна (чтобы знать, сколько займёт кнопка с текстом).</summary>
    private readonly Dictionary<UIElement, double> _labelWidths = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        var widths = new List<(UIElement Child, TextBlock? Label, double Full, double Compact)>();
        double height = 0;

        foreach (var child in children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            var label = Label(child);
            if (label is null)
            {
                widths.Add((child, null, child.DesiredSize.Width, child.DesiredSize.Width));
                continue;
            }

            if (label.Visibility == Visibility.Visible)
                _labelWidths[child] = label.DesiredSize.Width + label.Margin.Left + label.Margin.Right;
            var labelWidth = _labelWidths.GetValueOrDefault(child);
            var full = label.Visibility == Visibility.Visible ? child.DesiredSize.Width : child.DesiredSize.Width + labelWidth;
            widths.Add((child, label, full, full - labelWidth));
        }

        // Решение зависит только от ширин, а не от текущего состояния — раскладка устойчива.
        var available = availableSize.Width;
        var total = widths.Sum(w => w.Full);
        var compact = new HashSet<UIElement>();
        for (var i = widths.Count - 1; i >= 0 && total > available; i--)
        {
            if (widths[i].Label is null)
                continue;
            compact.Add(widths[i].Child);
            total -= widths[i].Full - widths[i].Compact;
        }

        foreach (var (child, label, _, _) in widths)
        {
            if (label is null)
                continue;
            var visibility = compact.Contains(child) ? Visibility.Collapsed : Visibility.Visible;
            if (label.Visibility != visibility)
            {
                label.Visibility = visibility;
                // Без текста команда узнаётся по подсказке.
                if (child is FrameworkElement { ToolTip: null } element && visibility == Visibility.Collapsed)
                    element.ToolTip = label.Text;
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            }
        }

        var width = children.Sum(c => c.DesiredSize.Width);
        return new Size(double.IsInfinity(available) ? width : Math.Min(width, available), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }

    /// <summary>Подпись кнопки со значком: первый TextBlock рядом с Image в содержимом кнопки.</summary>
    private static TextBlock? Label(UIElement child)
    {
        if (child is not ButtonBase { Content: Panel content })
            return null;
        var items = content.Children.Cast<UIElement>().ToList();
        return items.Any(i => i is Image) ? items.OfType<TextBlock>().FirstOrDefault() : null;
    }

    /// <summary>Обрезка как раньше, когда текст скрыт у всех кнопок.</summary>
    protected override Geometry GetLayoutClip(Size layoutSlotSize) =>
        new RectangleGeometry(new Rect(0, 0, Math.Min(ActualWidth, layoutSlotSize.Width), Math.Max(ActualHeight, layoutSlotSize.Height)));
}

/// <summary>
/// Окно не больше рабочей области экрана: размеры по умолчанию рассчитаны на большой монитор,
/// на маленьком окно уменьшается и остаётся по центру. Включается в стиле окна Pdm.Window.
/// </summary>
public static class WindowFit
{
    public static readonly DependencyProperty FitToScreenProperty = DependencyProperty.RegisterAttached(
        "FitToScreen", typeof(bool), typeof(WindowFit), new PropertyMetadata(false, OnChanged));

    public static bool GetFitToScreen(DependencyObject d) => (bool)d.GetValue(FitToScreenProperty);
    public static void SetFitToScreen(DependencyObject d, bool value) => d.SetValue(FitToScreenProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Window window && e.NewValue is true)
            window.SourceInitialized += (_, _) => Fit(window);
    }

    private static void Fit(Window window)
    {
        var area = SystemParameters.WorkArea;
        var maxWidth = area.Width * 0.95;
        var maxHeight = area.Height * 0.95;
        var changed = false;
        if (!double.IsNaN(window.Width) && window.Width > maxWidth)
        {
            window.Width = Math.Max(window.MinWidth, maxWidth);
            changed = true;
        }
        if (!double.IsNaN(window.Height) && window.Height > maxHeight)
        {
            window.Height = Math.Max(window.MinHeight, maxHeight);
            changed = true;
        }
        if (changed && window.WindowStartupLocation != WindowStartupLocation.Manual)
        {
            window.Left = area.Left + (area.Width - window.Width) / 2;
            window.Top = area.Top + (area.Height - window.Height) / 2;
        }
    }
}
