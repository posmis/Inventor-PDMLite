using System.Reflection;
using System.Runtime.InteropServices;

namespace PdmLite.AddIn;

/// <summary>
/// Точка входа, которую видит Inventor (ClassId/ClientId — как в PdmLite.addin).
/// Загружает плагин в изолированный контекст и передаёт ему вызовы Inventor.
/// </summary>
[Guid(ClassId)]
[ComVisible(true)]
public sealed class StandardAddInServer : Inv.ApplicationAddInServer
{
    public const string ClassId = "daf7f48d-0f53-40ea-8861-0d3fe0627c14";

    private const string CoreFolder = "core";
    private const string CoreAssembly = "PdmLite.AddIn.Core";
    private const string CoreType = "PdmLite.AddIn.Core.AddInServer";

    private Inv.ApplicationAddInServer? _core;

    public void Activate(Inv.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(typeof(StandardAddInServer).Assembly.Location)!, CoreFolder);
            var context = AddInLoadContext.Create(Path.Combine(folder, CoreAssembly + ".dll"));
            var assembly = context.LoadFromAssemblyName(new AssemblyName(CoreAssembly));
            var type = assembly.GetType(CoreType, throwOnError: true)!;
            _core = (Inv.ApplicationAddInServer)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            NativeMessageBox.Error($"PdmLite: не удалось загрузить плагин.\n\n{ex.GetBaseException().Message}");
            return;
        }

        _core.Activate(addInSiteObject, firstTime);
    }

    public void Deactivate()
    {
        _core?.Deactivate();
        _core = null;
    }

    public void ExecuteCommand(int commandID) => _core?.ExecuteCommand(commandID);

    public object? Automation => _core?.Automation;
}
