using PdmLite.Core.Events;
using PdmLite.Core.UI;

namespace PdmLite.Core.Modularity;

public interface IModuleContext
{
    HostKind Host { get; }
    IServiceProvider Services { get; }
    IEventBus Events { get; }
    IUiRegistry Ui { get; }
}
