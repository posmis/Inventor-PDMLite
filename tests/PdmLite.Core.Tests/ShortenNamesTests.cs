using ShortenNames;

namespace PdmLite.Core.Tests;

/// <summary>Утилита сокращения имён: подбор имён без файловой системы.</summary>
public class ShortenerTests
{
    private const string StepName = "Q1~TO-263-3_L10.2-W9.0-P5.08-LS15.2-BR~TO-263-3_L10.2-W9.0-P5.08-LS15.2-BR~GJMo_stp";

    [Fact]
    public void StepRepeats_Removed()
    {
        Assert.Equal("Q1~TO-263-3_L10.2-W9.0-P5.08-LS15.2-BR~GJMo_stp", Shortener.ShortenStem(StepName, 100));
    }

    [Theory]
    [InlineData("A~B~B~C", "A~B~C")]
    [InlineData("A~b~B~a", "A~b")]
    [InlineData("~~A~~", "A")]
    [InlineData("Без повторов", "Без повторов")]
    public void Repeats(string stem, string expected) => Assert.Equal(expected, Shortener.ShortenStem(stem, 100));

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(45)]
    public void TooLong_TruncatedWithCode_WithinLimit(int max)
    {
        var result = Shortener.ShortenStem(StepName, max);
        Assert.True(result.Length <= max, result);
        Assert.EndsWith("_" + Shortener.Code(StepName), result);
        Assert.StartsWith("Q1~TO-263", result);
        Assert.Equal(result, Shortener.ShortenStem(StepName, max)); // одинаково при повторе
    }

    [Fact]
    public void DifferentLongNames_DifferentResults()
    {
        var a = Shortener.ShortenStem(new string('x', 80) + "A", 30);
        var b = Shortener.ShortenStem(new string('x', 80) + "B", 30);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Code_IgnoresCase() => Assert.Equal(Shortener.Code("Abc"), Shortener.Code("aBC"));

    [Fact]
    public void Unique_AddsNumber_WithinLimit()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abcdefghij.ipt", "abcdefgh_2.ipt" };
        Assert.Equal("abcdefgh_3.ipt", Shortener.Unique("abcdefghij", ".ipt", 10, taken));
        Assert.Equal("free.ipt", Shortener.Unique("free", ".ipt", 10, taken));
    }

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "proj");

    [Fact]
    public void Plan_ByPathAndName_SameStemSameNewName_OldVersionsSkipped()
    {
        var folder = Path.Combine(Root, "Сборка платформа", "Прочие изделия");
        string[] files =
        [
            Path.Combine(folder, StepName + ".iam"),
            Path.Combine(folder, StepName + ".stp"),
            Path.Combine(folder, "Короткое.ipt"),
            Path.Combine(folder, "OldVersions", StepName + ".0001.iam"),
        ];

        var plan = Shortener.Plan(Root, files, maxPath: 259, maxName: 60);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, c => Assert.NotNull(c.To));
        Assert.Equal(Path.GetFileNameWithoutExtension(plan[0].To), Path.GetFileNameWithoutExtension(plan[1].To));
        Assert.All(plan, c => Assert.True(Path.GetFileName(c.To!).Length <= 60));
        Assert.All(plan, c => Assert.Contains("имя", c.Reason));
    }

    [Fact]
    public void Plan_PathLimit_UsesFolderLength()
    {
        var folder = Path.Combine(Root, new string('п', 150));
        var file = Path.Combine(folder, new string('и', 60) + ".ipt");
        var change = Assert.Single(Shortener.Plan(Root, [file], maxPath: 200, maxName: 100));
        Assert.True(change.To!.Length <= 200);
        Assert.Contains("путь", change.Reason);
    }

    [Fact]
    public void Plan_FolderTooLong_NoName()
    {
        var folder = Path.Combine(Root, new string('п', 195));
        var change = Assert.Single(Shortener.Plan(Root, [Path.Combine(folder, "abcdefghijklmnop.ipt")], maxPath: 200, maxName: 100));
        Assert.Null(change.To);
        Assert.Contains("папка", change.Reason);
    }

    [Fact]
    public void Plan_NewNameTakenByExistingFile_GetsNumber()
    {
        var existing = Path.Combine(Root, "A~B.ipt");
        var plan = Shortener.Plan(Root, [existing, Path.Combine(Root, "A~B~B~B~B~B~B~B~B~B~B~B~B~B~B~B~B.ipt")], maxPath: 259, maxName: 20);
        Assert.Equal("A~B_2.ipt", Assert.Single(plan).ToName);
    }

    [Fact]
    public void Plan_NothingLong_Empty() => Assert.Empty(Shortener.Plan(Root, [Path.Combine(Root, "a.ipt")], 200, 100));
}

/// <summary>Перепривязка ссылок: ссылки — строками в самих файлах (копия несёт ссылки, как настоящий документ).</summary>
public sealed class RelinkerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-shorten-").FullName;

    private sealed class FileLinks : IDocumentLinks
    {
        public string? FailReplaceFor { get; set; }
        public string? IgnoreFor { get; set; }
        public string? FailReadFor { get; set; }

        public IReadOnlyList<string> References(string path) =>
            FailReadFor is not null && path.Contains(FailReadFor)
                ? throw new IOException("не открывается")
                : File.ReadAllLines(path).Where(l => l.StartsWith("ref:")).Select(l => l[4..]).ToList();

        public int Replace(string path, IReadOnlyDictionary<string, string> byPath, IReadOnlyDictionary<string, string> byName)
        {
            if (FailReplaceFor is not null && path.Contains(FailReplaceFor))
                throw new IOException("не сохраняется");
            if (IgnoreFor is not null && path.Contains(IgnoreFor))
                return 0;
            var count = 0;
            var lines = File.ReadAllLines(path).Select(l =>
            {
                if (!l.StartsWith("ref:"))
                    return l;
                var reference = l[4..];
                if (byPath.TryGetValue(reference, out var to) || byName.TryGetValue(Path.GetFileName(reference), out to))
                {
                    count++;
                    return "ref:" + to;
                }
                return l;
            }).ToList();
            File.WriteAllLines(path, lines);
            return count;
        }
    }

    private string Create(string relative, params string[] references)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, references.Select(r => "ref:" + r));
        return path;
    }

    private Relinker Relinker(FileLinks links) => new(links, _ => { }) { BackupRoot = Path.Combine(_root, "_backup") };

    private static List<string> Refs(string path) => File.ReadAllLines(path).Where(l => l.StartsWith("ref:")).Select(l => l[4..]).ToList();

    [Fact]
    public void Rename_UpdatesParents_IncludingRenamedParent_AndRelativeNames()
    {
        var part = Create("Изделия/ДлиннаяДеталь.ipt");
        var sub = Create("Изделия/ДлиннаяСборка.iam", part);
        var top = Create("Верх.iam", sub, "ДлиннаяДеталь.ipt"); // вторая ссылка записана только именем
        var newPart = Path.Combine(_root, "Изделия", "Деталь.ipt");
        var newSub = Path.Combine(_root, "Изделия", "Сборка.iam");

        var result = Relinker(new FileLinks()).Run(_root, [new NameChange(part, newPart, ""), new NameChange(sub, newSub, "")]);

        Assert.Equal(2, result.Renamed);
        Assert.Equal(2, result.Documents);
        Assert.False(File.Exists(part) || File.Exists(sub));
        Assert.Equal([newSub, newPart], Refs(top));
        Assert.Equal([newPart], Refs(newSub));
        Assert.True(File.Exists(Path.Combine(result.BackupFolder, "_журнал.txt")));
    }

    [Fact]
    public void ReplaceFails_RolledBack()
    {
        var part = Create("A.ipt");
        var top = Create("Top.iam", part);
        var before = File.ReadAllText(top);
        var newPart = Path.Combine(_root, "B.ipt");

        var error = Assert.Throws<RelinkException>(() => Relinker(new FileLinks { FailReplaceFor = "Top" }).Run(_root, [new NameChange(part, newPart, "")]));

        Assert.Contains("отменены", error.Message);
        Assert.True(File.Exists(part));
        Assert.False(File.Exists(newPart));
        Assert.Equal(before, File.ReadAllText(top));
    }

    [Fact]
    public void NotReplacedSilently_Detected_RolledBack()
    {
        var part = Create("A.ipt");
        var top = Create("Top.iam", part);
        var newPart = Path.Combine(_root, "B.ipt");

        var error = Assert.Throws<RelinkException>(() => Relinker(new FileLinks { IgnoreFor = "Top" }).Run(_root, [new NameChange(part, newPart, "")]));

        Assert.Contains("не заменились", error.Message);
        Assert.Equal([part], Refs(top));
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void TargetExists_Refused_NothingChanged()
    {
        var part = Create("A.ipt");
        var other = Create("B.ipt");
        Assert.Throws<InvalidOperationException>(() => Relinker(new FileLinks()).Run(_root, [new NameChange(part, other, "")]));
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void ReadOnlyParent_Refused()
    {
        var part = Create("A.ipt");
        var top = Create("Top.iam", part);
        File.SetAttributes(top, FileAttributes.ReadOnly);
        var error = Assert.Throws<InvalidOperationException>(() => Relinker(new FileLinks()).Run(_root, [new NameChange(part, Path.Combine(_root, "B.ipt"), "")]));
        Assert.Contains("только для чтения", error.Message);
    }

    [Fact]
    public void UnreadableDocument_Warning_OthersProcessed()
    {
        var part = Create("A.ipt");
        var top = Create("Top.iam", part);
        Create("Битый.iam");
        var newPart = Path.Combine(_root, "B.ipt");

        var result = Relinker(new FileLinks { FailReadFor = "Битый" }).Run(_root, [new NameChange(part, newPart, "")]);

        Assert.Contains(result.Warnings, w => w.Contains("Битый"));
        Assert.Equal([newPart], Refs(top));
    }

    [Fact]
    public void SameOldNameInTwoFolders_NotMatchedByName()
    {
        var a = Create("X/Деталь.ipt");
        var b = Create("Y/Деталь.ipt");
        var top = Create("Top.iam", "Деталь.ipt"); // только имя — неизвестно, какая из двух
        var result = Relinker(new FileLinks()).Run(_root,
            [new NameChange(a, Path.Combine(_root, "X", "Д1.ipt"), ""), new NameChange(b, Path.Combine(_root, "Y", "Д2.ipt"), "")]);
        Assert.Equal(["Деталь.ipt"], Refs(top));
        Assert.Equal(0, result.Documents);
    }

    [Fact]
    public void OldVersions_NotTreatedAsParents()
    {
        var part = Create("A.ipt");
        var old = Create("OldVersions/Top.0001.iam", part);
        var result = Relinker(new FileLinks()).Run(_root, [new NameChange(part, Path.Combine(_root, "B.ipt"), "")]);
        Assert.Equal(0, result.Documents);
        Assert.Equal([part], Refs(old));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
