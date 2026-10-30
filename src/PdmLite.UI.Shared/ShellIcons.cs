using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdmLite.UI.Shared;

/// <summary>
/// Иконки типов файлов из Windows — те же, что показывает Проводник
/// (для .ipt/.iam/.idw/.dwg их регистрирует Inventor при установке).
/// </summary>
/// <remarks>
/// Иконка берётся по расширению, без обращения к самому файлу, и кешируется.
/// Вызывать в потоке интерфейса.
/// </remarks>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<(string Extension, bool Large), ImageSource?> Cache = new();

    /// <summary>Иконка типа файла или <c>null</c>, если Windows её не отдала.</summary>
    /// <param name="extension">Расширение с точкой: <c>.ipt</c>.</param>
    /// <param name="large">32×32 вместо 16×16.</param>
    public static ImageSource? ForExtension(string extension, bool large = false) =>
        string.IsNullOrEmpty(extension)
            ? null
            : Cache.GetOrAdd((extension.ToLowerInvariant(), large), key => Load(key.Extension, key.Large));

    private static ImageSource? Load(string extension, bool large)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var info = new ShFileInfo();
        var flags = ShgfiIcon | ShgfiUseFileAttributes | (large ? ShgfiLargeIcon : ShgfiSmallIcon);
        try
        {
            if (SHGetFileInfo("file" + extension, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == IntPtr.Zero
                || info.hIcon == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null; // нет иконки — вызывающий код покажет свою
        }
        finally
        {
            if (info.hIcon != IntPtr.Zero)
                DestroyIcon(info.hIcon);
        }
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint ShgfiSmallIcon = 0x1;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
