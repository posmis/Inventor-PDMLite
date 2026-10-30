using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdmLite.UI.Shared;

namespace PdmLite.AddIn;

/// <summary>
/// Иконки для ленты Inventor. Лента принимает IPictureDisp, поэтому векторная
/// иконка темы растеризуется в нужный размер (16 — малая, 32 — большая).
/// </summary>
internal static class RibbonIcons
{
    public static object? Create(string key, int size)
    {
        if (PdmIcons.Get(key) is not { } source)
            return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        using var image = new System.Drawing.Bitmap(stream);
        return PictureConverter.ToPictureDisp(image);
    }

    /// <summary>Доступ к защищённому преобразованию Image → IPictureDisp из WinForms.</summary>
    private sealed class PictureConverter : System.Windows.Forms.AxHost
    {
        private PictureConverter() : base(string.Empty)
        {
        }

        public static object ToPictureDisp(System.Drawing.Image image) => GetIPictureDispFromPicture(image);
    }
}
