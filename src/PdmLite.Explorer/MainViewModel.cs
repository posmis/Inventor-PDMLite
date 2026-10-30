using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;
using PdmLite.Core.Threading;
using PdmLite.Hosting;
using PdmLite.UI.Shared;

namespace PdmLite.Explorer;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly PdmHost _host;
    private readonly JsonSettings<ExplorerSettings> _settings;
    private readonly IProjectSession _session;

    public MainViewModel(PdmHost host, JsonSettings<ExplorerSettings> settings)
    {
        _host = host;
        _settings = settings;
        _session = host.Services.GetRequiredService<IProjectSession>();
        var ui = host.Services.GetRequiredService<IUiDispatcher>();

        RecentProjects = new ObservableCollection<string>(settings.Value.RecentProjects);

        host.Events.Subscribe<ProjectOpened>(e => ui.Post(() => OnProjectOpened(e.Project)));
        host.Events.Subscribe<InventorConnectionChanged>(e => ui.Post(() =>
        {
            IsInventorConnected = e.IsConnected;
            InventorStatus = e.IsConnected ? $"Inventor {e.InventorVersion}" : "Inventor: нет связи";
        }));
        host.Events.Subscribe<ActiveDocumentChanged>(e => ui.Post(() =>
            ActiveDocument = e.Path is null ? "" : Path.GetFileName(e.Path)));
    }

    public PdmHost Host => _host;

    public ObservableCollection<string> RecentProjects { get; }

    [ObservableProperty] private string _title = "PdmLite Explorer";
    [ObservableProperty] private string _projectPath = "Проект не открыт";
    [ObservableProperty] private bool _isInventorConnected;
    [ObservableProperty] private string _inventorStatus = "Inventor: нет связи";
    [ObservableProperty] private string _activeDocument = "";

    [RelayCommand]
    private Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Папка проекта" };
        return dialog.ShowDialog() == true ? OpenProjectAsync(dialog.FolderName) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task OpenProjectFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "Файл проекта Inventor", Filter = "Проект Inventor (*.ipj)|*.ipj" };
        return dialog.ShowDialog() == true ? OpenProjectAsync(dialog.FileName) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task OpenRecentAsync(string path) => OpenProjectAsync(path);

    [RelayCommand]
    private Task RefreshAsync() => _session.RefreshAsync();

    public async Task OpenProjectAsync(string path)
    {
        try
        {
            await _session.OpenAsync(ProjectInfo.FromPath(path));
        }
        catch (Exception ex)
        {
            UiMessages.Error($"Не удалось открыть проект:\n{path}", ex);
        }
    }

    private void OnProjectOpened(ProjectInfo project)
    {
        Title = $"{project.Name} — PdmLite Explorer";
        ProjectPath = project.ProjectFile ?? project.RootFolder;

        _settings.Value.AddRecent(ProjectPath);
        try
        {
            _settings.Save();
        }
        catch (IOException)
        {
            // Не критично: список недавних проектов просто не сохранится.
        }

        RecentProjects.Clear();
        foreach (var path in _settings.Value.RecentProjects)
            RecentProjects.Add(path);
    }
}
