using System.Runtime.InteropServices;

namespace PdmLite.Modules.InventorLink;

/// <summary>
/// Windows не даёт чужому процессу выводить своё окно на передний план. Explorer заранее
/// разрешает это, чтобы после «Вставить в Inventor» окно Inventor стало активным.
/// </summary>
internal static class ForegroundPermission
{
    private const int AnyProcess = -1;

    public static void AllowAny()
    {
        if (OperatingSystem.IsWindows())
            AllowSetForegroundWindow(AnyProcess);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
