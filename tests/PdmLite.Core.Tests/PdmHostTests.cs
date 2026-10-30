using Microsoft.Extensions.DependencyInjection;
using PdmLite.Core.Documents;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.UI;
using PdmLite.Hosting;

namespace PdmLite.Core.Tests;

public class PdmHostTests
{
    private sealed class FakeLauncher : IDocumentLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Модуль заменяет сервис хоста и регистрирует команду.</summary>
    private sealed class FakeModule : IPdmModule
    {
        public bool Initialized { get; private set; }
        public bool Stopped { get; private set; }
        public ModuleInfo Info { get; } = new("Fake", "Fake", new Version(1, 0));

        public void ConfigureServices(IServiceCollection services, HostKind host) =>
            services.AddSingleton<IDocumentLauncher, FakeLauncher>();

        public void Initialize(IModuleContext context)
        {
            Initialized = true;
            context.Ui.RegisterCommand(new CommandDescriptor("Fake.Cmd", "Команда", CommandPlacement.Toolbar, _ => Task.CompletedTask));
        }

        public void Shutdown() => Stopped = true;
    }

    [Fact]
    public void Host_LoadsModules_AndManagesLifecycle()
    {
        var module = new FakeModule();
        var folder = Directory.CreateTempSubdirectory("pdmlite-host-").FullName;
        try
        {
            using (var host = PdmHost.Create(new PdmHostOptions
                   {
                       Host = HostKind.Explorer,
                       ModulesFolder = folder,
                       ExtraModules = [module],
                       FileLogging = false,
                   }))
            {
                host.Start();

                Assert.True(module.Initialized);
                Assert.IsType<FakeLauncher>(host.Services.GetRequiredService<IDocumentLauncher>());
                Assert.NotNull(host.Services.GetRequiredService<IProjectSession>());
                Assert.Single(host.Ui.GetCommands(CommandPlacement.Toolbar));
                Assert.Contains(host.Modules, m => m.Info.Id == "Fake" && m.IsLoaded);
            }

            Assert.True(module.Stopped);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
