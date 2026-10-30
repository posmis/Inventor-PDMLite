using System.Reflection;
using System.Windows;
using System.Windows.Interop;

namespace PdmLite.AddIn.Core;

internal static class DockableWindowHost
{
    private const int WmGetDlgCode = 0x0087;
    private const int DlgcWantArrows = 0x0001;
    private const int DlgcWantTab = 0x0002;
    private const int DlgcWantAllKeys = 0x0004;
    private const int DlgcWantChars = 0x0080;

    /// <summary>
    /// Разрешает ввод с клавиатуры в WPF-содержимом закрепляемого окна Inventor.
    /// </summary>
    /// <remarks>
    /// Закрепляемое окно Inventor обрабатывает клавиши как диалог (IsDialogMessage): символы
    /// не доходят до окна, которое на WM_GETDLGCODE не ответило, что хочет их получать.
    /// Окно WPF по умолчанию так не отвечает — поэтому в поле поиска не вводился текст.
    /// </remarks>
    /// <param name="onEnabled">Вызывается, когда обработчик подключён (для журнала).</param>
    public static void EnableKeyboardInput(FrameworkElement content, Action? onEnabled = null)
    {
        HwndSource? hooked = null;
        content.Loaded += (_, _) =>
        {
            // Окно WPF создаётся вместе с ElementHost; Loaded может прийти повторно.
            if (PresentationSource.FromVisual(content) is HwndSource source && !ReferenceEquals(source, hooked))
            {
                source.AddHook(WantAllKeys);
                hooked = source;
                onEnabled?.Invoke();
            }
        };
    }

    private static IntPtr WantAllKeys(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetDlgCode)
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(DlgcWantChars | DlgcWantArrows | DlgcWantTab | DlgcWantAllKeys);
    }

    /// <summary>
    /// Встраивает окно Win32 в закрепляемое окно Inventor. Тип параметра HWND
    /// в <c>DockableWindow.AddChild</c> различается между версиями сборки Inventor API
    /// (int/long), поэтому значение приводится к фактическому типу параметра.
    /// </summary>
    public static void AddChild(Inv.DockableWindow window, IntPtr handle)
    {
        var method = typeof(Inv.DockableWindow).GetMethod(nameof(Inv.DockableWindow.AddChild))
                     ?? throw new MissingMethodException(nameof(Inv.DockableWindow), nameof(Inv.DockableWindow.AddChild));
        var parameterType = method.GetParameters()[0].ParameterType;

        object value = parameterType == typeof(long) ? handle.ToInt64()
            : parameterType == typeof(int) ? handle.ToInt32()
            : parameterType == typeof(IntPtr) ? handle
            : Convert.ChangeType(handle.ToInt64(), parameterType);

        try
        {
            method.Invoke(window, [value]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
