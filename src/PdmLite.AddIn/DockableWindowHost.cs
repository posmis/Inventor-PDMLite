using System.Reflection;

namespace PdmLite.AddIn;

internal static class DockableWindowHost
{
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
