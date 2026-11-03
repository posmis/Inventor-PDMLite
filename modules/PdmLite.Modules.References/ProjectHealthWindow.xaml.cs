using System.ComponentModel;
using System.Windows;
using PdmLite.Core.Domain;
using PdmLite.Index;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.References;

/// <summary>Окно привязано к проекту на момент открытия, даже если пользователь переключит проект.</summary>
public partial class ProjectHealthWindow : Window
{
    private readonly ProjectHealthService _service;
    private readonly ProjectInfo _project;
    private CancellationTokenSource? _cts;
    private ProjectHealthCard? _card;

    public ProjectHealthWindow(ProjectHealthService service, ProjectInfo project, bool analyze)
    {
        InitializeComponent();
        _service = service;
        _project = project;
        ProjectText.Text = project.Name;
        LocationText.Text = project.RootFolder;
        Loaded += async (_, _) => { if (analyze) await AnalyzeAsync(); else await LoadAsync(); };
    }

    private async Task WorkAsync(Func<CancellationToken, Task> action)
    {
        if (_cts is not null) return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        AnalyzeButton.IsEnabled = ReloadButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        BusyProgress.Visibility = Visibility.Visible;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { ProgressText.Text = "Остановлено. Сохранённая карточка не изменена."; }
        catch (Exception ex) { ProgressText.Text = "Не удалось завершить проверку: " + ex.Message; }
        finally
        {
            _cts = null;
            AnalyzeButton.IsEnabled = ReloadButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            BusyProgress.Visibility = Visibility.Collapsed;
        }
    }

    private Task LoadAsync() => WorkAsync(async ct =>
    {
        ProgressText.Text = "Загрузка карточки…";
        var card = await _service.LoadAsync(_project, ct);
        if (card is null)
        {
            StatusText.Text = "Карточка ещё не сформирована. Запустите полный анализ проекта.";
            ProgressText.Text = "";
            return;
        }
        ShowCard(card);
        ProgressText.Text = "Проверка актуальности сохранённой карточки…";
        // Обход файлов и хеширование не должны блокировать интерфейс.
        try
        {
            var current = await Task.Run(() => _service.IsCurrentAsync(card, ct), ct);
            StatusText.Text = current ? "Карточка актуальна — " + card.Summary
                : "Карточка устарела: проект изменился. Запустите полный анализ для обновления.";
            ProgressText.Text = "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { StatusText.Text = "Актуальность не подтверждена. " + ex.Message; ProgressText.Text = ""; }
    });

    private Task AnalyzeAsync() => WorkAsync(async ct =>
    {
        ProgressText.Text = "Начинается полный анализ…";
        var progress = new Progress<string>(text => ProgressText.Text = text);
        var card = await Task.Run(() => _service.AnalyzeAsync(_project, progress, ct), ct);
        ShowCard(card);
        StatusText.Text = "Анализ завершён — " + card.Summary;
        ProgressText.Text = "Последняя карточка сохранена. При изменениях проекта выполните анализ заново.";
    });

    private void ShowCard(ProjectHealthCard card)
    {
        _card = card;
        IssuesGrid.ItemsSource = card.Issues;
        SummaryText.Text = $"Сформирована: {card.CreatedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}. " +
            $"Документов: {card.Files}, прочитано: {card.Read}. Деталей: {card.Parts}, сборок: {card.Assemblies}, " +
            $"чертежей: {card.Drawings}, презентаций: {card.Presentations}. Ошибок: {card.Errors}, замечаний: {card.Warnings}.";
        SavedText.Text = "Сохранённая карточка: " + _service.CardPath(_project);
    }

    private async void Analyze_OnClick(object sender, RoutedEventArgs e) => await AnalyzeAsync();
    private async void Reload_OnClick(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Stop_OnClick(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void ShowFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is ProjectHealthIssue issue && File.Exists(issue.Path))
            UiClipboard.ShowInExplorer(issue.Path);
    }
    private void Copy_OnClick(object sender, RoutedEventArgs e)
    {
        if (_card is not null)
            UiClipboard.SetText(string.Join(Environment.NewLine, _card.Issues.Select(i => $"{i.Severity}\t{i.Category}\t{i.Path}\t{i.Details}")));
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        // Сначала остановить работу: закрытое окно не должно позднее перезаписать карточку.
        if (_cts is not null) { _cts.Cancel(); e.Cancel = true; }
        base.OnClosing(e);
    }
}
