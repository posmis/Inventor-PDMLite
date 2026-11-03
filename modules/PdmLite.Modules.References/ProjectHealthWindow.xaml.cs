using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
    private ListCollectionView? _view;
    private bool _ready;
    private sealed record CheckFilter(string? Category, string Title, int Count);

    public ProjectHealthWindow(ProjectHealthService service, ProjectInfo project, bool analyze)
    {
        InitializeComponent();
        _service = service;
        _project = project;
        ProjectText.Text = project.Name;
        LocationText.Text = project.RootFolder;
        LocationText.ToolTip = project.RootFolder;
        ProjectText.ToolTip = project.Name;
        _ready = true;
        EmptyText.Text = "Откройте сохранённую карточку или запустите анализ.";
        EmptyText.Visibility = Visibility.Visible;
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
                : "Карточка устарела: изменился проект или состав проверок. Запустите анализ заново.";
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
        var category = (CategoryList.SelectedItem as CheckFilter)?.Category;
        var rows = card.Issues.Select(i => new ProjectHealthRow(i, _project.RootFolder)).ToList();
        _view = new ListCollectionView(rows);
        IssuesGrid.ItemsSource = _view;
        var checks = new List<CheckFilter> { new(null, "Все проверки", rows.Count) };
        checks.AddRange(rows.GroupBy(r => r.Category).OrderByDescending(g => g.Any(r => r.Severity == "Ошибка"))
            .ThenBy(g => g.Key).Select(g => new CheckFilter(g.Key, g.Key, g.Count())));
        CategoryList.ItemsSource = checks;
        CategoryList.SelectedItem = checks.FirstOrDefault(c => c.Category == category) ?? checks[0];
        AllFilter.Content = $"Все ({card.Issues.Count})";
        ErrorFilter.Content = $"Ошибки ({card.Errors})";
        WarningFilter.Content = $"Замечания ({card.Warnings})";
        SummaryText.Text = $"Документов: {card.Files} · Прочитано: {card.Read} · " +
            $"Детали: {card.Parts} · Сборки: {card.Assemblies} · Чертежи: {card.Drawings} · Презентации: {card.Presentations}";
        SummaryText.ToolTip = SummaryText.Text;
        SavedText.Text = $"Карточка сохранена {card.CreatedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
        SavedText.ToolTip = _service.CardPath(_project);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_ready || _view is null) return;
        var severity = ErrorFilter.IsChecked == true ? "Ошибка" : WarningFilter.IsChecked == true ? "Предупреждение" : null;
        var category = (CategoryList.SelectedItem as CheckFilter)?.Category;
        var selected = IssuesGrid.SelectedItem;
        _view.Filter = item => ((ProjectHealthRow)item).Matches(SearchBox.Text, severity, category);
        ResultText.Text = $"Показано: {_view.Count} из {_card!.Issues.Count}";
        EmptyText.Text = _card.Issues.Count == 0 ? "Замечаний нет. Проверки пройдены."
            : "Ничего не найдено. Измените поиск или фильтры.";
        EmptyText.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IssuesGrid.SelectedItem = selected is not null && _view.Contains(selected) ? selected : _view.Cast<object>().FirstOrDefault();
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (!_ready) return;
        var row = IssuesGrid.SelectedItem as ProjectHealthRow;
        DetailTitle.Text = row is null ? "Выберите замечание в списке" : $"{row.Severity} · {row.Category}";
        DetailPath.Text = row?.Path ?? "";
        DetailMessage.Text = row?.Details ?? "";
        ShowFileButton.IsEnabled = row is not null && File.Exists(row.Path);
        CopyPathButton.IsEnabled = CopyDetailButton.IsEnabled = row is not null;
    }

    private async void Analyze_OnClick(object sender, RoutedEventArgs e) => await AnalyzeAsync();
    private async void Reload_OnClick(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Stop_OnClick(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void Filter_OnClick(object sender, RoutedEventArgs e) => ApplyFilter();
    private void Category_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void Search_OnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ClearSearch_OnClick(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
    }
    private void Search_OnKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; } }
    private void Issue_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();
    private void Issue_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Двойной щелчок по строке, а не по заголовку или пустому месту.
        if (ItemsControl.ContainerFromElement(IssuesGrid, e.OriginalSource as DependencyObject) is DataGridRow)
            ShowFile_OnClick(sender, e);
    }
    private void ShowFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is ProjectHealthRow row && File.Exists(row.Path))
            UiClipboard.ShowInExplorer(row.Path);
    }
    private static string CopyText(ProjectHealthRow row) => $"{row.Severity}\t{row.Category}\t{row.Path}\t{row.Details}";
    private void Copy_OnClick(object sender, RoutedEventArgs e)
    {
        if (_view is not null)
            UiClipboard.SetText(string.Join(Environment.NewLine, _view.Cast<ProjectHealthRow>().Select(CopyText)));
    }
    private void CopyPath_OnClick(object sender, RoutedEventArgs e)
    { if (IssuesGrid.SelectedItem is ProjectHealthRow row) UiClipboard.SetText(row.Path); }
    private void CopyDetail_OnClick(object sender, RoutedEventArgs e)
    { if (IssuesGrid.SelectedItem is ProjectHealthRow row) UiClipboard.SetText(CopyText(row)); }
    protected override void OnClosing(CancelEventArgs e)
    {
        // Сначала остановить работу: закрытое окно не должно позднее перезаписать карточку.
        if (_cts is not null) { _cts.Cancel(); e.Cancel = true; }
        base.OnClosing(e);
    }
}
