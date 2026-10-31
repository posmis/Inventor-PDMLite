using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PdmLite.Data;
using PdmLite.Library.Import;
using PdmLite.Modules.Library.Rules;
using PdmLite.Modules.Library.Settings;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Import;

public partial class ImportWindow : Window
{
    private readonly ImportViewModel _viewModel;
    private readonly IServiceProvider _services;

    public ImportWindow(ImportViewModel viewModel, IServiceProvider services)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _services = services;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Без подключения к базе добавлять некуда — сначала настройки.
        if (_services.GetRequiredService<IPdmDatabase>().IsConfigured)
            return;
        UiMessages.Info("Сначала укажите подключение к базе данных и папку библиотеки.");
        if (!LibrarySettingsWindow.ShowDialog(_services, this))
            Close();
    }

    private IEnumerable<ImportRowViewModel> SelectedRows => Grid.SelectedItems.OfType<ImportRowViewModel>();

    private async void AddFiles_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Детали и сборки для библиотеки",
            Filter = "Детали и сборки Inventor (*.ipt;*.iam)|*.ipt;*.iam",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddAsync(dialog.FileNames);
    }

    private async void AddFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка с компонентами (с подпапками)", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddAsync(dialog.FolderNames);
    }

    private void Exclude_OnClick(object sender, RoutedEventArgs e) => _viewModel.SetInclude(SelectedRows, false);

    private void Include_OnClick(object sender, RoutedEventArgs e) => _viewModel.SetInclude(SelectedRows, true);

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void ShowFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row)
            UiClipboard.ShowInExplorer(row.SourcePath);
    }

    private void CopyDesignation_OnClick(object sender, RoutedEventArgs e) =>
        UiClipboard.SetText(string.Join(Environment.NewLine, SelectedRows.Select(r => r.Designation)));

    private void CopyPath_OnClick(object sender, RoutedEventArgs e) =>
        UiClipboard.SetText(string.Join(Environment.NewLine, SelectedRows.Select(r => r.SourcePath)));

    private List<ImportRowViewModel>? SelectionOrWarn()
    {
        var rows = SelectedRows.ToList();
        if (rows.Count > 0)
            return rows;
        UiMessages.Info("Выделите строки в таблице (Ctrl+A — все).");
        return null;
    }

    private void Source_OnClick(object sender, RoutedEventArgs e)
    {
        SourceButton.ContextMenu!.PlacementTarget = SourceButton;
        SourceButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SourceButton.ContextMenu.IsOpen = true;
    }

    private void SourceItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectionOrWarn() is not { } rows)
            return;
        var source = Enum.Parse<DesignationSource>((string)((FrameworkElement)sender).Tag);
        var changed = _viewModel.SetSource(rows, source);
        if (changed < rows.Count)
            UiMessages.Info($"Изменено строк: {changed} из {rows.Count}. В остальных файлах выбранное поле пустое.");
    }

    private void BulkEdit_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectionOrWarn() is { } rows)
            new BulkEditWindow(_viewModel, rows) { Owner = this }.ShowDialog();
    }

    private async void Rules_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new RulesWindow(_services.GetRequiredService<RulesViewModel>()) { Owner = this };
        if (window.ShowDialog() == true)
            await _viewModel.ReloadRulesAsync();
    }

    private async void OpenList_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Rows.Count > 0
            && MessageBox.Show(this, "Текущий список будет закрыт (несохранённые правки пропадут). Продолжить?", UiMessages.Caption,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "Список подготовки",
            Filter = $"Список PdmLite (*{ImportList.Extension})|*{ImportList.Extension}",
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.LoadListAsync(dialog.FileName);
    }

    private void SaveList_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить список подготовки",
            Filter = $"Список PdmLite (*{ImportList.Extension})|*{ImportList.Extension}",
            FileName = _viewModel.ListPath is { } path ? System.IO.Path.GetFileName(path) : "Подготовка" + ImportList.Extension,
            InitialDirectory = _viewModel.ListPath is { } p ? System.IO.Path.GetDirectoryName(p) : null,
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.SaveList(dialog.FileName);
    }
}
