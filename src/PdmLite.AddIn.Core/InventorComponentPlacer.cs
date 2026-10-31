using System.Runtime.InteropServices;
using PdmLite.Core.Documents;
using PdmLite.Core.Threading;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Вставка компонента штатной командой Inventor «Вставить компонент»: файл передаётся команде
/// заранее, пользователь указывает место щелчком (можно вставить несколько экземпляров).
/// </summary>
public sealed class InventorComponentPlacer(Inv.Application inventor, IUiDispatcher ui) : IComponentPlacer
{
    private const string PlaceComponentCommand = "AssemblyPlaceComponentCmd";

    public bool CanPlace => true;

    public Task PlaceAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл компонента не найден.", path);

        return ui.InvokeAsync(() =>
        {
            if (inventor.ActiveDocument is not { DocumentType: Inv.DocumentTypeEnum.kAssemblyDocumentObject })
                throw new InvalidOperationException("Активный документ — не сборка. Откройте или активируйте сборку, в которую нужно вставить компонент.");
            if (string.Equals(((Inv._Document)inventor.ActiveDocument).FullFileName, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Нельзя вставить сборку саму в себя. Активируйте другую сборку.");

            var commands = inventor.CommandManager;
            commands.PostPrivateEvent(Inv.PrivateEventTypeEnum.kFileNameEvent, path);
            commands.ControlDefinitions[PlaceComponentCommand].Execute();
            SetForegroundWindow(new IntPtr(inventor.MainFrameHWND));
        });
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
