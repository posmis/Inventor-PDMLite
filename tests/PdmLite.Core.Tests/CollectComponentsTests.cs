using CollectComponents;

namespace PdmLite.Core.Tests;

/// <summary>Утилита сбора файлов: поиск только в папке, зависимости на всех уровнях, отчёт.</summary>
public sealed class CollectComponentsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-collect-").FullName;

    private string Add(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
        return path;
    }

    /// <summary>Подмена Apprentice: ссылки задаются по имени файла.</summary>
    private sealed class FakeReferences(Dictionary<string, DocumentReference[]> map) : IReferenceReader
    {
        public List<string> Read_ { get; } = [];

        public IReadOnlyList<DocumentReference> Read(string path)
        {
            Read_.Add(Path.GetFileName(path));
            return map.TryGetValue(Path.GetFileName(path), out var refs) ? refs : [];
        }
    }

    [Fact]
    public void ParseNames_TakesFileNames_SkipsEmptyAndRepeats()
    {
        var names = Collector.ParseNames(["Блок.iam", "", "  D:\\Электроника\\Плата.ipt  ", "блок.IAM", "Разъём.ipt\tD:\\old", "\"Корпус.ipt\""]);
        Assert.Equal(["Блок.iam", "Плата.ipt", "Разъём.ipt", "Корпус.ipt"], names);
    }

    [Fact]
    public void Collect_FollowsDependencies_AllLevels_OnlyInSourceFolder()
    {
        var source = Path.Combine(_root, "Компоненты");
        Add("Компоненты/Электроника/Блок питания.iam");
        Add("Компоненты/Электроника/Платы/Плата.iam");
        Add("Компоненты/Электроника/Платы/Чип.ipt");
        Add("Компоненты/Крепёж/Винт M3.ipt");
        Add("Компоненты/OldVersions/Плата.iam"); // резервная копия Inventor — не берём
        Add("Другая папка/Пропавший.ipt"); // есть по старому пути, но не в папке поиска
        var references = new FakeReferences(new()
        {
            // У потерянных ссылок полного пути нет — только имя.
            ["Блок питания.iam"] = [new("Плата.iam", null), new("Винт M3.ipt", "C:\\Старое\\Винт M3.ipt"),
                new("Пропавший.ipt", Path.Combine(_root, "Другая папка", "Пропавший.ipt"))],
            ["Плата.iam"] = [new("Чип.ipt", null), new("Винт M3.ipt", null)],
        });
        var log = new List<string>();

        var result = new Collector(source).Collect(["Блок питания.iam"], references, log.Add);

        Assert.Equal(["Блок питания.iam", "Плата.iam", "Винт M3.ipt", "Чип.ipt"], result.Found.Select(f => Path.GetFileName(f.Path)));
        Assert.All(result.Found, f => Assert.StartsWith(source, f.Path));
        Assert.Equal("Блок питания.iam", result.Found[1].Reason);
        var missing = Assert.Single(result.Missing);
        Assert.Contains("Пропавший.ipt (зависимость Блок питания.iam", missing);
        Assert.Contains(log, l => l.Contains("Блок питания.iam: зависимостей 3"));
        Assert.Contains(log, l => l.Contains("- Пропавший.ipt  (нет в папке)"));
        Assert.Equal(1, references.Read_.Count(n => n == "Винт M3.ipt")); // каждый файл читается один раз
    }

    [Fact]
    public void Collect_DuplicateNames_TakesNewest_WithWarning_AndCopyWritesReport()
    {
        var source = Path.Combine(_root, "Компоненты");
        var older = Add("Компоненты/А/Деталь.ipt");
        var newer = Add("Компоненты/Б/Деталь.ipt");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
        File.SetAttributes(newer, FileAttributes.ReadOnly);
        var collector = new Collector(source);

        var result = collector.Collect(["Деталь.ipt", "Нет такой.ipt"], references: null, _ => { });

        Assert.Equal(newer, Assert.Single(result.Found).Path);
        Assert.Contains(result.Warnings, w => w.Contains("найдено 2"));

        var target = Path.Combine(_root, "Собрано");
        var report = collector.Copy(result, target);
        var copy = Path.Combine(target, "Деталь.ipt");
        Assert.True(File.Exists(copy));
        Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
        Assert.Contains("Нет такой.ipt (из списка)", File.ReadAllText(report));
        File.SetAttributes(newer, FileAttributes.Normal);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
