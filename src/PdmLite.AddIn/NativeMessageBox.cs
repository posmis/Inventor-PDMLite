using System.Runtime.InteropServices;

namespace PdmLite.AddIn;

/// <summary>Сообщение об ошибке без WPF/WinForms: загрузчик не зависит от UI-библиотек.</summary>
internal static class NativeMessageBox
{
    private const uint MbIconError = 0x10;

    public static void Error(string text) => MessageBoxW(IntPtr.Zero, text, "PdmLite", MbIconError);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
