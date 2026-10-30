using PdmLite.Data.Library;
using PdmLite.Library.Items;

namespace PdmLite.Library.Tests;

/// <summary>Чем заменить ненайденную ссылку сборки — без базы и без файлов (проверка файлов подменена).</summary>
public class ReferenceResolverTests
{
    // Полные пути строятся от корня текущей ОС: тесты идут и в Windows, и в Linux.
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Lib");
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "Проект");
    private const string Ours = "PdmLite";
    private static readonly Guid Ring = Guid.NewGuid();
    private const string Now = "Крепёж/Кольца стопорные/DIN 471/Кольцо DIN 471-15x1.ipt";
    private const string Before = "Крепёж/Кольца стопорные/DIN 471-15/Кольцо DIN 471-15x1.ipt";

    private static readonly LibraryPathIndex Index = new([(Ring, Now, "Кольцо DIN 471-15x1")], [(Before, Ring)]);

    private static string Full(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>На диске есть только текущий файл кольца и <paramref name="also"/>.</summary>
    private static Func<string, bool> Disk(params string[] also) =>
        path => path == Full(Now) || also.Contains(path);

    [Fact]
    public void ExistingFile_IsNeverReplaced()
    {
        var old = Full(Before);
        Assert.Null(LibraryReferenceResolver.Resolve(Index, Root, old, null, Ours, Disk(old)));
        Assert.Null(LibraryReferenceResolver.Resolve(Index, Root, Before, Ours, Ours, Disk(Full(Before))));
    }

    [Fact]
    public void MovedComponent_FullPath_OrLibraryRelative()
    {
        Assert.Equal(Full(Now), LibraryReferenceResolver.Resolve(Index, Root, Full(Before), null, Ours, Disk()));
        Assert.Equal(Full(Now), LibraryReferenceResolver.Resolve(Index, Root, Before, Ours, Ours, Disk()));
    }

    [Fact]
    public void ProjectRelativeReference_OnlyByOldPath_NotByFileName()
    {
        // Путь относительно проекта: существует ли файл, не знаем — по имени файла не подменяем.
        Assert.Null(LibraryReferenceResolver.Resolve(Index, Root, @"Детали\Кольцо DIN 471-15x1.ipt", null, Ours, Disk()));
        Assert.Equal(Full(Now), LibraryReferenceResolver.Resolve(Index, Root, Before, null, Ours, Disk()));
    }

    [Fact]
    public void MissingProjectFile_WithLibraryName_FoundByFileName()
    {
        // Файл лежал в проекте и пропал — тот же компонент есть в библиотеке.
        Assert.Equal(Full(Now), LibraryReferenceResolver.Resolve(Index, Root, Path.Combine(Project, "Кольцо DIN 471-15x1.ipt"), null, Ours, Disk()));
        Assert.Null(LibraryReferenceResolver.Resolve(Index, Root, Path.Combine(Project, "Рама.ipt"), null, Ours, Disk()));
    }

    [Fact]
    public void OtherLibrary_OnlyByOldPath()
    {
        Assert.Null(LibraryReferenceResolver.Resolve(Index, Root, "Кольцо DIN 471-15x1.ipt", "Content Center", Ours, Disk()));
        Assert.Equal(Full(Now), LibraryReferenceResolver.Resolve(Index, Root, Before, "Content Center", Ours, Disk()));
    }

    [Fact]
    public void CurrentFileMissing_ReturnsNull()
    {
        Assert.Null(LibraryReferenceResolver.Resolve(Index, Root, Full(Before), null, Ours, _ => false));
    }

    [Theory]
    [InlineData("/a/b.ipt", null, true)]
    [InlineData("a/b.ipt", "PdmLite", true)]
    [InlineData("a/b.ipt", "pdmlite", true)]
    [InlineData("a/b.ipt", null, false)]
    [InlineData("a/b.ipt", "Content Center", false)]
    public void ShouldReload_OnlyWhenFileIsSurelyMissing(string reference, string? library, bool expected) =>
        Assert.Equal(expected, LibraryReferenceResolver.ShouldReload(reference, library, Ours));

    [Fact]
    public void MovesFromLog_ChainsMerges_AndSkipsBrokenEntries()
    {
        var bolt = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        LibraryLogEntry Edit(Guid id, string before, string after, int minute) => new()
        {
            User = "u", Action = "edit", ItemId = id, At = t.AddMinutes(minute),
            Details = $$$"""{"before":{"RelativePath":"{{{before}}}"},"after":{"RelativePath":"{{{after}}}"}}""",
        };

        var moves = LibraryPathIndex.MovesFromLog(
        [
            Edit(bolt, "a/1.ipt", "a/2.ipt", 1),
            Edit(bolt, "a/2.ipt", "a/3.ipt", 2),
            Edit(bolt, "a/3.ipt", "a/3.ipt", 3), // путь не менялся
            new LibraryLogEntry { User = "u", Action = "edit", ItemId = bolt, At = t, Details = "{не json" },
            new LibraryLogEntry { User = "u", Action = "merge", ItemId = kept, At = t, Details = """{"RelativePath":"b/old.ipt"}""" },
            new LibraryLogEntry { User = "u", Action = "delete", ItemId = bolt, At = t, Details = """{"RelativePath":"x.ipt"}""" },
        ]).ToList();

        Assert.Equal([("a/1.ipt", bolt), ("a/2.ipt", bolt), ("b/old.ipt", kept)], moves.OrderBy(m => m.OldPath));

        var index = new LibraryPathIndex([(bolt, "a/3.ipt", "Болт"), (kept, "b/new.ipt", "Гайка")], moves);
        Assert.Equal("a/3.ipt", index.Find("a/1.ipt", false)); // через два переезда — сразу на текущий
        Assert.Equal("b/new.ipt", index.Find(Path.Combine(Root, "b", "old.ipt"), false));
    }

    [Fact]
    public void OldPath_TakenByAnotherItem_IsNotRedirected()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // Компонент «a» переехал, а на его старое место встал «b».
        var index = new LibraryPathIndex([(a, "new/a.ipt", "A"), (b, "old/a.ipt", "B")], [("old/a.ipt", a)]);
        Assert.Null(index.Find("old/a.ipt", false));
    }
}
