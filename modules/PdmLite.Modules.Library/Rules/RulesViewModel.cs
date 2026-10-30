using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdmLite.Data.Library;
using PdmLite.Library.Designations;
using PdmLite.Library.Rules;

namespace PdmLite.Modules.Library.Rules;

/// <summary>Правило замены в таблице окна правил.</summary>
public sealed partial class RuleRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _pattern = "";
    [ObservableProperty] private string _replacement = "";
    [ObservableProperty] private bool _isRegex;
    [ObservableProperty] private string _comment = "";

    public DesignationRule ToRule(int order) => new()
    {
        SortOrder = order,
        Enabled = Enabled,
        Pattern = Pattern,
        Replacement = Replacement,
        IsRegex = IsRegex,
        Comment = Comment,
    };

    public static RuleRowViewModel From(DesignationRule rule) => new()
    {
        Enabled = rule.Enabled,
        Pattern = rule.Pattern,
        Replacement = rule.Replacement ?? "",
        IsRegex = rule.IsRegex,
        Comment = rule.Comment ?? "",
    };
}

/// <summary>Окно правил замены: список по порядку применения и проверка на примере.</summary>
public sealed partial class RulesViewModel : ObservableObject
{
    private readonly DesignationRuleService _service;

    public RulesViewModel(DesignationRuleService service)
    {
        _service = service;
        Rows.CollectionChanged += (_, e) =>
        {
            // Перемещение строки — не добавление: подписку не дублируем; удалённые отписываем.
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            {
                foreach (var row in e.OldItems?.OfType<RuleRowViewModel>() ?? [])
                    row.PropertyChanged -= OnRowChanged;
                foreach (var row in e.NewItems?.OfType<RuleRowViewModel>() ?? [])
                    row.PropertyChanged += OnRowChanged;
            }
            UpdateTest();
        };
    }

    public ObservableCollection<RuleRowViewModel> Rows { get; } = [];

    [ObservableProperty] private RuleRowViewModel? _selected;
    [ObservableProperty] private string _testInput = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private string _errors = "";

    public async Task LoadAsync()
    {
        foreach (var rule in await _service.ListAsync())
            Rows.Add(RuleRowViewModel.From(rule));
    }

    public IReadOnlyList<DesignationRule> ToRules() => Rows.Select((r, i) => r.ToRule(i)).ToList();

    public Task SaveAsync() => _service.SaveAsync(ToRules());

    public void Move(RuleRowViewModel row, int delta)
    {
        var index = Rows.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Rows.Count)
            return;
        Rows.Move(index, target);
        Selected = row;
    }

    partial void OnTestInputChanged(string value) => UpdateTest();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateTest();

    /// <summary>Пример: как подготовится обозначение (единый вид → правила → единый вид), и ошибки в правилах.</summary>
    private void UpdateTest()
    {
        var rules = ToRules();
        Errors = string.Join("\n", rules.Select((r, i) => (Index: i, Error: DesignationRuleSet.Validate(r)))
            .Where(x => x.Error is not null).Select(x => $"Правило {x.Index + 1}: {x.Error}"));

        if (TestInput.Trim().Length == 0)
        {
            TestResult = "";
            return;
        }
        var set = new DesignationRuleSet(rules);
        var (replaced, applied) = set.ApplyReplacements(DesignationNormalizer.ToCanonical(TestInput));
        var result = DesignationNormalizer.ToCanonical(replaced);
        var standard = StandardParser.Find(result);
        TestResult = $"Обозначение: {result}\nСтандарт: {standard ?? "не найден"}\n"
                     + (applied.Count > 0 ? "Применены: " + string.Join("; ", applied) : "Правила не применялись");
    }
}
