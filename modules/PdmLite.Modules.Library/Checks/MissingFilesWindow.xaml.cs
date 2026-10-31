using System.Windows;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Checks;

/// <summary>
/// Ненайденные файлы активной сборки (со всеми подсборками) — прямо из Inventor: только имена, каждый один раз,
/// сколько раз стоит в сборке и где его искали. Для поиска и возврата перенесённых файлов.
/// </summary>
public partial class MissingFilesWindow : Window
{
    private readonly IAssemblyComponents _assembly;
    private readonly ILogger _logger;
    private List<MissingRow> _rows = [];

    public MissingFilesWindow(IAssemblyComponents assembly, ILogger<MissingFilesWindow> logger)
    {
        InitializeComponent();
        _assembly = assembly;
        _logger = logger;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var inspection = await _assembly.InspectActiveAsync();
            _rows = inspection.Components.Where(c => c.IsMissing)
                .Select(c => new MissingRow(c.Path, c.Count))
                .OrderBy(r => r.Name, NaturalComparer.Instance).ToList();
            Grid.ItemsSource = _rows;
            HeaderText.Text = Path.GetFileName(inspection.AssemblyPath);
            SummaryText.Text = _rows.Count == 0
                ? "Все файлы сборки найдены."
                : $"Не найдено файлов: {_rows.Count} (вхождений: {_rows.Sum(r => r.Count)}).";
        }
        catch (Exception ex)
        {
            // Обработчик async void внутри Inventor: ошибка не должна выйти наружу.
            _logger.LogWarning(ex, "Не удалось получить ненайденные файлы");
            HeaderText.Text = "";
            SummaryText.Text = ex.Message;
        }
    }

    private IEnumerable<MissingRow> Selected => Grid.SelectedItems.OfType<MissingRow>();

    private void CopyNames_OnClick(object sender, RoutedEventArgs e) => UiClipboard.SetText(string.Join(Environment.NewLine, _rows.Select(r => r.Name)));

    private void CopyPaths_OnClick(object sender, RoutedEventArgs e) => UiClipboard.SetText(string.Join(Environment.NewLine, _rows.Select(r => r.Path)));

    private void CopySelectedNames_OnClick(object sender, RoutedEventArgs e) =>
        UiClipboard.SetText(string.Join(Environment.NewLine, Selected.Select(r => r.Name)));

    private void CopySelectedPaths_OnClick(object sender, RoutedEventArgs e) =>
        UiClipboard.SetText(string.Join(Environment.NewLine, Selected.Select(r => r.Path)));

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    public sealed record MissingRow(string Path, int Count)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    }
}
