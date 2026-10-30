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

    /// <summary>Сервис, который умеет освобождаться только асинхронно (как InventorConnection).</summary>
    private sealed class AsyncOnlyService : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Disposed = true;
        }
    }

    /// <summary>Модуль заменяет сервис хоста и регистрирует команду.</summary>
    private sealed class FakeModule : IPdmModule
    {
        public bool Initialized { get; private set; }
        public bool Stopped { get; private set; }
        public ModuleInfo Info { get; } = new("Fake", "Fake", new Version(1, 0));

        public void ConfigureServices(IServiceCollection services, HostKind host)
        {
            services.AddSingleton<IDocumentLauncher, FakeLauncher>();
            services.AddSingleton<AsyncOnlyService>();
        }

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
        AsyncOnlyService? asyncService;
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
                Assert.False(host.Services.GetRequiredService<IComponentPlacer>().CanPlace); // по умолчанию вставки нет
                Assert.Single(host.Ui.GetCommands(CommandPlacement.Toolbar));
                Assert.Contains(host.Modules, m => m.Info.Id == "Fake" && m.IsLoaded);
                asyncService = host.Services.GetRequiredService<AsyncOnlyService>();
            }

            Assert.True(module.Stopped);
            Assert.True(asyncService.Disposed);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
