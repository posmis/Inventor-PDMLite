using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Modularity;
using PdmLite.Hosting;

namespace PdmLite.Core.Tests;

public class ModuleLoaderTests
{
    private sealed class FakeModule(string id, HostKind hosts = HostKind.All, params string[] dependsOn) : IPdmModule
    {
        public ModuleInfo Info { get; } = new(id, id, new Version(1, 0), hosts, dependsOn);
        public void ConfigureServices(IServiceCollection services, HostKind host) { }
        public void Initialize(IModuleContext context) { }
        public void Shutdown() { }
    }

    private static readonly ModuleLoader Loader = new(NullLogger.Instance);

    private static ModulesConfig Config(params (string Id, bool Enabled)[] entries) => new()
    {
        Modules = entries.Select(e => new ModulesConfig.ModuleEntry { Id = e.Id, Enabled = e.Enabled }).ToList(),
    };

    [Fact]
    public void Resolve_OrdersDependenciesFirst()
    {
        var (ordered, _) = Loader.Resolve(
            [new FakeModule("Rename", HostKind.All, "Index"), new FakeModule("Index")], HostKind.Explorer, Config());

        Assert.Equal(["Index", "Rename"], ordered.Select(m => m.Info.Id));
    }

    [Fact]
    public void Resolve_DisabledDependency_SkipsDependentModule()
    {
        var (ordered, statuses) = Loader.Resolve(
            [new FakeModule("Rename", HostKind.All, "Index"), new FakeModule("Index")], HostKind.Explorer, Config(("Index", false)));

        Assert.Empty(ordered);
        Assert.Equal(ModuleState.Disabled, statuses.Single(s => s.Info.Id == "Index").State);
        Assert.Equal(ModuleState.MissingDependency, statuses.Single(s => s.Info.Id == "Rename").State);
    }

    [Fact]
    public void Resolve_SkipsModulesForOtherHost()
    {
        var (ordered, statuses) = Loader.Resolve(
            [new FakeModule("InventorLink", HostKind.Explorer), new FakeModule("Tree")], HostKind.Inventor, Config());

        Assert.Equal(["Tree"], ordered.Select(m => m.Info.Id));
        Assert.Equal(ModuleState.UnsupportedHost, statuses.Single(s => s.Info.Id == "InventorLink").State);
    }

    [Fact]
    public void Resolve_Cycle_IsNotLoaded()
    {
        var (ordered, _) = Loader.Resolve(
            [new FakeModule("A", HostKind.All, "B"), new FakeModule("B", HostKind.All, "A")], HostKind.Explorer, Config());

        Assert.Empty(ordered);
    }
}
