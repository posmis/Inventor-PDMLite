using Microsoft.Extensions.DependencyInjection;
using PdmLite.Core.Modularity;
using PdmLite.Core.UI;

namespace PdmLite.Modules.Properties;

public sealed class PropertiesModule : IPdmModule
{
    private DocumentCardViewModel? _viewModel;

    public ModuleInfo Info { get; } = new("Properties", "Свойства документа", new Version(0, 1, 0));

    public void ConfigureServices(IServiceCollection services, HostKind host)
    {
        services.AddSingleton<DocumentCardViewModel>();
    }

    public void Initialize(IModuleContext context)
    {
        _viewModel = context.Services.GetRequiredService<DocumentCardViewModel>();
        _viewModel.Attach();

        context.Ui.RegisterPanel(new PanelDescriptor(
            "Properties.Card", "Свойства", PanelPlacement.Details,
            sp => new DocumentCardView { DataContext = sp.GetRequiredService<DocumentCardViewModel>() },
            Icon: IconKeys.Info));
    }

    public void Shutdown() => _viewModel?.Detach();
}
