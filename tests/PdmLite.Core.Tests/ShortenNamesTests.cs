using ShortenNames;

namespace PdmLite.Core.Tests;

/// <summary>Утилита сокращения имён: подбор имён без файловой системы.</summary>
public class ShortenerTests
{
    private const string StepName = "Q1~TO-263-3_L10.2-W9.0-P5.08-LS15.2-BR~TO-263-3_L10.2-W9.0-P5.08-LS15.2-BR~GJMo_stp";

    [Fact]
    public void StepRepeats_Removed()
    {
        Assert.Equal("Q1~TO-263-3_L10.2-W9.0-P5.08-LS15.2-BR~GJMo", Shortener.ShortenStem(StepName, 100));
    }

    [Theory]
    [InlineData("A~B~B~C", "A~B~C")]
    [InlineData("A~b~B~a", "A~b")]
    [InlineData("~~A~~", "A")]
    [InlineData("Без повторов", "Без повторов")]
    public void Repeats(string stem, string expected) => Assert.Equal(expected, Shortener.ShortenStem(stem, 100));

    [Theory]
    [InlineData("GJMo_stp", "GJMo")]
    [InlineData("GJMo_STEP", "GJMo")]
    [InlineData("Корпус.stp", "Корпус")]
    [InlineData("Плата-igs", "Плата")]
    [InlineData("Деталь_x_t", "Деталь")]
    [InlineData("_stp", "_stp")] // одна приписка без имени — не трогаем
    [InlineData("Stepper", "Stepper")]
    [InlineData("Ластп", "Ластп")]
    public void FormatSuffix_Removed(string part, string expected) => Assert.Equal(expected, Shortener.WithoutFormat(part));

    [Fact]
    public void FormatSuffix_InEverySegment()
    {
        Assert.Equal("A~B", Shortener.ShortenStem("A_stp~B_stp~B", 100));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
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

    /// <summary>Строка «ref:a|b|c» — одна ссылка с вариантами записи (полный путь, относительное, логическое имя).</summary>
    private sealed class FileLinks : IDocumentLinks
    {
        public string? FailReplaceFor { get; set; }
        public string? IgnoreFor { get; set; }
        public string? FailReadFor { get; set; }

        public IReadOnlyList<IReadOnlyList<string>> Links(string path) =>
            FailReadFor is not null && path.Contains(FailReadFor)
                ? throw new IOException("Ссылка преобразована в путь, имеющий недопустимую длину")
                : File.ReadAllLines(path).Where(l => l.StartsWith("ref:")).Select(l => (IReadOnlyList<string>)l[4..].Split('|')).ToList();

        public int Replace(string path, Func<IReadOnlyList<string>, string?> target)
        {
            if (FailReplaceFor is not null && path.Contains(FailReplaceFor))
                throw new IOException("не сохраняется");
            if (IgnoreFor is not null && path.Contains(IgnoreFor))
                return 0;
            var count = 0;
            var lines = File.ReadAllLines(path).Select(l =>
            {
                if (!l.StartsWith("ref:") || target(l[4..].Split('|')) is not { } to)
                    return l;
                count++;
                return "ref:" + to;
            }).ToList();
            File.WriteAllLines(path, lines);
            return count;
        }
    }

    private string Create(string relative, params string[] references)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, references.Select(r => "ref:" + r));
        return path;
    }

    private string P(string relative) => Path.GetFullPath(Path.Combine(_root, relative));

    private Relinker Relinker(FileLinks links) => new(links, _ => { }) { BackupRoot = Path.Combine(_root, "_backup") };

    private RelinkResult Run(FileLinks links, params NameChange[] changes)
    {
        var relinker = Relinker(links);
        return relinker.Execute(_root, changes, relinker.Prepare(_root, changes));
    }

    private static List<string> Refs(string path) => File.ReadAllLines(path).Where(l => l.StartsWith("ref:")).Select(l => l[4..]).ToList();

    [Fact]
    public void Rename_UpdatesParents_IncludingRenamedParent_AndNameOnlyReferences()
    {
        var part = Create("Изделия/ДлиннаяДеталь.ipt");
        var sub = Create("Изделия/ДлиннаяСборка.iam", part);
        var top = Create("Верх.iam", sub, "ДлиннаяДеталь.ipt"); // вторая ссылка записана только именем
        var newPart = P("Изделия/Деталь.ipt");
        var newSub = P("Изделия/Сборка.iam");

        var result = Run(new FileLinks(), new NameChange(part, newPart, ""), new NameChange(sub, newSub, ""));

        Assert.Equal(2, result.Renamed);
        Assert.Equal(2, result.Documents);
        Assert.Equal(3, result.References);
        Assert.False(File.Exists(part) || File.Exists(sub));
        Assert.Equal([newSub, newPart], Refs(top));
        Assert.Equal([newPart], Refs(newSub));
        Assert.True(File.Exists(Path.Combine(result.BackupFolder, "_журнал.txt")));
        Assert.True(File.Exists(Path.Combine(result.BackupFolder, ShortenNames.Relinker.ChangesFile)));
    }

    [Fact]
    public void FullPathUnreadable_RelativeVariantUsed()
    {
        var part = Create("Изделия/ДлиннаяДеталь.ipt");
        // Полного пути Inventor не дал (мусор), есть относительный от папки сборки.
        var top = Create("Верх.iam", "???|Изделия/ДлиннаяДеталь.ipt");
        var newPart = P("Изделия/Д.ipt");
        Run(new FileLinks(), new NameChange(part, newPart, ""));
        Assert.Equal([newPart], Refs(top));
    }

    [Fact]
    public void SameNameOtherExistingFile_NotTouched()
    {
        var part = Create("X/Деталь.ipt");
        var other = Create("Y/Деталь.ipt");
        var top = Create("Top.iam", other, part);
        var newPart = P("X/Д.ipt");
        Run(new FileLinks(), new NameChange(part, newPart, ""));
        Assert.Equal([other, newPart], Refs(top)); // ссылка на другую «Деталь.ipt» осталась
    }

    [Fact]
    public void UnreadableDocument_BlocksRenaming_NothingChanged()
    {
        var part = Create("A.ipt");
        var top = Create("Top.iam", part);
        Create("Битый.iam");
        var relinker = Relinker(new FileLinks { FailReadFor = "Битый" });
        var changes = new[] { new NameChange(part, P("B.ipt"), "") };

        var plan = relinker.Prepare(_root, changes);
        Assert.Single(plan.Unreadable);
        var error = Assert.Throws<InvalidOperationException>(() => relinker.Execute(_root, changes, plan));

        Assert.Contains("Не прочитаны", error.Message);
        Assert.True(File.Exists(part));
        Assert.Equal([part], Refs(top));
    }

    [Fact]
    public void Unreferenced_Reported()
    {
        var part = Create("Одинокая.ipt");
        var plan = Relinker(new FileLinks()).Prepare(_root, [new NameChange(part, P("O.ipt"), "")]);
        Assert.Equal([part], plan.Unreferenced);
        Assert.Empty(plan.Parents);
    }

    [Fact]
    public void ReplaceFails_RolledBack()
    {
        var part = Create("A.ipt");
        var top = Create("Top.iam", part);
        var before = File.ReadAllText(top);
        var newPart = P("B.ipt");

        var error = Assert.Throws<RelinkException>(() => Run(new FileLinks { FailReplaceFor = "Top" }, new NameChange(part, newPart, "")));

        Assert.Contains("отменены", error.Message);
        Assert.True(File.Exists(part));
        Assert.False(File.Exists(newPart));
        Assert.Equal(before, File.ReadAllText(top));
    }

    [Fact]
    public void NothingReplaced_IsError_RolledBack()
    {
        var part = Create("A.ipt");
        var top = Create("Top.iam", part);
        var error = Assert.Throws<RelinkException>(() => Run(new FileLinks { IgnoreFor = "Top" }, new NameChange(part, P("B.ipt"), "")));
        Assert.Contains("не заменились", error.Message);
        Assert.Equal([part], Refs(top));
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void TargetExists_Refused_NothingChanged()
    {
        var part = Create("A.ipt");
        var other = Create("B.ipt");
        Assert.Throws<InvalidOperationException>(() => Run(new FileLinks(), new NameChange(part, other, "")));
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void ReadOnlyParent_Refused()
    {
        var part = Create("A.ipt");
        var top = Create("Top.iam", part);
        File.SetAttributes(top, FileAttributes.ReadOnly);
        var error = Assert.Throws<InvalidOperationException>(() => Run(new FileLinks(), new NameChange(part, P("B.ipt"), "")));
        Assert.Contains("только для чтения", error.Message);
    }

    [Fact]
    public void SameOldNameInTwoFolders_NotMatchedByNameOnly()
    {
        var a = Create("X/Деталь.ipt");
        var b = Create("Y/Деталь.ipt");
        var top = Create("Top.iam", "Деталь.ipt"); // только имя — неизвестно, какая из двух
        var result = Run(new FileLinks(), new NameChange(a, P("X/Д1.ipt"), ""), new NameChange(b, P("Y/Д2.ipt"), ""));
        Assert.Equal(["Деталь.ipt"], Refs(top));
        Assert.Equal(0, result.Documents);
    }

    [Fact]
    public void OldVersions_NotTreatedAsParents()
    {
        var part = Create("A.ipt");
        var old = Create("OldVersions/Top.0001.iam", part);
        var result = Run(new FileLinks(), new NameChange(part, P("B.ipt"), ""));
        Assert.Equal(0, result.Documents);
        Assert.Equal([part], Refs(old));
    }

    [Fact]
    public void RelinkOnly_ByJournal_FilesAlreadyRenamed()
    {
        // Прошлый запуск переименовал файлы, но ссылки остались старыми.
        var part = Create("A.ipt");
        var sub = Create("Sub.iam", part);
        var top = Create("Top.iam", sub);
        var newPart = P("A2.ipt");
        var newSub = P("Sub2.iam");
        File.Move(part, newPart);
        File.Move(sub, newSub);
        var journal = Directory.CreateDirectory(P("_old")).FullName;
        File.WriteAllText(Path.Combine(journal, "_журнал.txt"),
            $"Сокращение имён{Environment.NewLine}ПЕРЕИМЕНОВАНИЕ (2):{Environment.NewLine}  {part}{Environment.NewLine}    → A2.ipt{Environment.NewLine}  {sub}{Environment.NewLine}    → Sub2.iam{Environment.NewLine}");

        var changes = ShortenNames.Relinker.LoadChanges(Path.Combine(journal, "_журнал.txt"));
        Assert.Equal(2, changes.Count);
        var relinker = Relinker(new FileLinks());
        var result = relinker.Execute(_root, changes, relinker.Prepare(_root, changes), relinkOnly: true);

        Assert.Equal(0, result.Renamed);
        Assert.Equal([newSub], Refs(top));
        Assert.Equal([newPart], Refs(newSub));

        // Новый журнал читается из JSON.
        Assert.Equal(changes.Select(c => c.To), ShortenNames.Relinker.LoadChanges(result.BackupFolder).Select(c => c.To));
    }

    [Fact]
    public void Matcher_NewReference_NotMatchedAgain()
    {
        var matcher = new Relinker.Matcher([new NameChange(P("a.ipt"), P("b.ipt"), "")]);
        Assert.Equal(P("b.ipt"), matcher.Target([P("a.ipt")], _root));
        Assert.Null(matcher.Target([P("b.ipt")], _root));
        Assert.Equal(P("b.ipt"), matcher.Target(["a.ipt"], _root));
        Assert.Null(matcher.Target([P("c.ipt")], _root));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
