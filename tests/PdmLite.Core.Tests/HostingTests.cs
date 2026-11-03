using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Modularity;
using PdmLite.Hosting;

namespace PdmLite.Core.Tests;

public sealed class HostingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-hosting-").FullName;

    public sealed class Settings
    {
        public string Name { get; set; } = "по умолчанию";
        public List<int> Values { get; set; } = [];
    }

    [Fact]
    public void JsonSettings_RoundTrip_AndCorruptFileFallsBackToDefaults()
    {
        var settings = new JsonSettings<Settings>("test", _root);
        settings.Value.Name = "Проект";
        settings.Value.Values.Add(5);
        settings.Save();

        var loaded = new JsonSettings<Settings>("test", _root).Value;
        Assert.Equal("Проект", loaded.Name);
        Assert.Equal([5], loaded.Values);
        Assert.Contains("Проект", File.ReadAllText(settings.FilePath)); // кириллица не экранируется

        File.WriteAllText(settings.FilePath, "{ не json");
        Assert.Equal("по умолчанию", new JsonSettings<Settings>("test", _root).Value.Name);
    }

    [Fact]
    public void ModulesConfig_AcceptsComments_AndBrokenFileEnablesEverything()
    {
        var path = Path.Combine(_root, "modules.json");
        File.WriteAllText(path, """
            {
              // комментарий
              "Modules": [ { "Id": "Library", "Enabled": false }, ],
            }
            """);
        var config = ModulesConfig.Load(path);
        Assert.False(config.IsEnabled("library"));
        Assert.True(config.IsEnabled("Other"));
        Assert.Null(config.LoadError);

        File.WriteAllText(path, "{ сломано");
        var broken = ModulesConfig.Load(path);
        Assert.NotNull(broken.LoadError);
        Assert.True(broken.IsEnabled("Library"));
        Assert.True(ModulesConfig.Load(Path.Combine(_root, "нет.json")).IsEnabled("Library"));
    }

    [Fact]
    public void ModuleLoader_Discover_EmptyFolder_FindsNothing() =>
        Assert.Empty(new ModuleLoader(NullLogger.Instance).Discover(_root));

    private sealed class ThrowingModule(string id, bool inConfigure) : IPdmModule
    {
        public ModuleInfo Info { get; } = new(id, id, new Version(1, 0));

        public void ConfigureServices(IServiceCollection services, HostKind host)
        {
            if (inConfigure)
                throw new InvalidOperationException("сбой регистрации");
        }

        public void Initialize(IModuleContext context)
        {
            if (!inConfigure)
                throw new InvalidOperationException("сбой запуска");
        }

        public void Shutdown() { }
    }

    private sealed class GoodModule : IPdmModule
    {
        public bool Started { get; private set; }
        public ModuleInfo Info { get; } = new("Good", "Good", new Version(1, 0));
        public void ConfigureServices(IServiceCollection services, HostKind host) { }
        public void Initialize(IModuleContext context) => Started = true;
        public void Shutdown() { }
    }

    [Fact]
    public void Host_FailingModules_DoNotStopOthers()
    {
        var good = new GoodModule();
        using var host = PdmHost.Create(new PdmHostOptions
        {
            Host = HostKind.Explorer,
            ModulesFolder = _root,
            FileLogging = false,
            ExtraModules = [new ThrowingModule("BadConfigure", true), new ThrowingModule("BadStart", false), good],
        });
        host.Start();

        Assert.True(good.Started);
        Assert.Equal(ModuleState.Failed, host.Modules.Single(m => m.Info.Id == "BadStart").State);
        Assert.Contains("сбой запуска", host.Modules.Single(m => m.Info.Id == "BadStart").Reason);
        Assert.Equal(ModuleState.Failed, host.Modules.Single(m => m.Info.Id == "BadConfigure").State);
        Assert.Contains("сбой регистрации", host.Modules.Single(m => m.Info.Id == "BadConfigure").Reason);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
