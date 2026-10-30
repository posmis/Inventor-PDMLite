using System.Windows;
using PdmLite.Hosting;

namespace PdmLite.Explorer;

public partial class ModulesWindow : Window
{
    public ModulesWindow(IEnumerable<ModuleStatus> modules)
    {
        InitializeComponent();
        List.ItemsSource = modules.Select(m => new { m.Info, StateText = Describe(m) }).ToList();
    }

    private static string Describe(ModuleStatus status) => status.State switch
    {
        ModuleState.Loaded => "Работает",
        ModuleState.Disabled => "Отключён",
        _ => $"Не загружен: {status.Reason}",
    };
}
