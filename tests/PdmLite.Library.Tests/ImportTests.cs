using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public class ImportTests
{
    private static LibraryImportService Service(TestDatabase db, FakePropertyStore store, TempFolders folders) =>
        new(db.Database, store, () => new LibraryStorage(folders.Library), NullLogger<LibraryImportService>.Instance);

    [PostgresFact]
    public async Task Analyze_SkipsBackups_NormalizesAndSuggestsCategory()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var bolt = folders.AddFile(@"ГОСТ 7798-70/Болт M10-6gx25.109(S17) ГОСТ 7798-70.ipt", "bolt");
        folders.AddFile(@"ГОСТ 7798-70/OldVersions/Болт M10-6gx25.109(S17) ГОСТ 7798-70.0003.ipt", "old");
        store.Files[bolt] = new DocumentProperties { PartNumber = "Болт М10-6gх25.109 ГОСТ 7798-70", Description = "Болт М10-6gх25.109 ГОСТ 7798-70", Title = "Болт" };

        var session = await Service(db, store, folders).AnalyzeAsync([folders.Source]);

        var c = Assert.Single(session.Candidates);
        Assert.Single(session.Skipped);
        Assert.Equal("Болт M10-6gx25.109 ГОСТ 7798-70", c.Designation);
        Assert.Equal("ГОСТ 7798-70", c.Standard);
        Assert.Equal("Крепёж/Болты", session.Catalog.Get(c.CategoryId)!.Path);
        Assert.Equal("Крепёж/Болты/ГОСТ 7798-70/Болт M10-6gx25.109 ГОСТ 7798-70.ipt", c.TargetRelativePath);
        Assert.Equal(ImportStatus.Warning, c.Status); // Наименование «Болт» расходится с обозначением
        Assert.Contains(c.Issues, i => i.Severity == ImportSeverity.Warning && i.Message.Contains("расходятся"));
    }

    [PostgresFact]
    public async Task Commit_CopiesWritesPropertiesAndRegisters_ThenRejectsDuplicates()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var nut = folders.AddFile("Гайка M8-6H.10.016 ГОСТ 5915-70.ipt", "nut");
        var service = Service(db, store, folders);

        var session = await service.AnalyzeAsync([nut]);
        var result = await service.CommitAsync(session);

        Assert.Equal(1, result.Added);
        var target = Path.Combine(folders.Library, "Крепёж", "Гайки", "ГОСТ 5915-70", "Гайка M8-6H.10.016 ГОСТ 5915-70.ipt");
        Assert.True(File.Exists(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("Гайка M8-6H.10.016 ГОСТ 5915-70", store.Written[target].PartNumber);
        Assert.Equal("Крепёж/Гайки", store.Written[target].Custom[LibraryProperties.Category]);

        await using (var context = db.Database.CreateContext())
        {
            var item = await context.LibraryItems.Include(i => i.Versions).SingleAsync();
            Assert.Equal("ГОСТ 5915-70", item.Standard);
            Assert.Single(item.Versions);
            Assert.NotEqual(item.SourceHash, item.FileHash);
            Assert.Equal(1, await context.LibraryLog.CountAsync());
        }

        // Тот же исходник повторно — дубликат и по обозначению, и по файлу.
        var again = await service.AnalyzeAsync([nut]);
        var c = Assert.Single(again.Candidates);
        Assert.Equal(ImportStatus.Error, c.Status);
        Assert.Contains(c.Issues, i => i.Message.Contains("уже есть"));
        Assert.Contains(c.Issues, i => i.Message.Contains("уже добавлен"));
    }

    [PostgresFact]
    public async Task Validate_FindsDuplicatesInBatch_AndMissingCategoryOrStandard()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var a = folders.AddFile("a/Шайба A.10.01.08кп.016 ГОСТ 11371-78.ipt", "a");
        var b = folders.AddFile("b/Шайба А.10.01.08кп.016 ГОСТ 11371-78.ipt", "b"); // кириллическая «А» — то же обозначение
        var plug = folders.AddFile("Изделие неизвестное 123.ipt", "x");

        var session = await Service(db, store, folders).AnalyzeAsync([folders.Source]);

        Assert.All(session.Candidates.Where(c => c.SourcePath != plug), c =>
            Assert.Contains(c.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains("повторяется")));
        var unknown = session.Candidates.Single(c => c.SourcePath == plug);
        Assert.Equal(LibraryItemKind.Purchased, unknown.Kind);
        Assert.Contains(unknown.Issues, i => i.Message.Contains("Не выбран раздел"));

        // Исключили один из дубликатов — второй становится готовым.
        session.Candidates.Single(c => c.SourcePath == b).Include = false;
        Service(db, store, folders).Validate(session);
        Assert.True(session.Candidates.Single(c => c.SourcePath == a).CanImport);
    }

    [PostgresFact]
    public async Task Commit_LearnsCorrectedCategory_ForNextImports()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var ring = folders.AddFile("Кольцо 42 ГОСТ 13943-86.ipt", "ring42");
        var service = Service(db, store, folders);

        var session = await service.AnalyzeAsync([ring]);
        var c = Assert.Single(session.Candidates);
        Assert.Equal("Уплотнения/Кольца уплотнительные", session.Catalog.Get(c.CategoryId)!.Path);
        c.CategoryId = session.Catalog.FindByPath("Крепёж/Кольца стопорные")!.Id; // пользователь исправил
        Assert.Equal(1, (await service.CommitAsync(session)).Added);

        var next = folders.AddFile("Кольцо 52 ГОСТ 13943-86.ipt", "ring52");
        var second = await service.AnalyzeAsync([next]);
        Assert.Equal("Крепёж/Кольца стопорные", second.Catalog.Get(Assert.Single(second.Candidates).CategoryId)!.Path);
    }

    [PostgresFact]
    public async Task Commit_RelinksAssemblyToLibraryCopies_AndRollsBackFailedItem()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var body = folders.AddFile("Разъём/Корпус X1 Harting.ipt", "body");
        var pin = folders.AddFile("Разъём/Вилка X1 Harting.ipt", "pin");
        var asm = folders.AddFile("Разъём/Разъём X1 Harting.iam", "asm");
        store.Files[asm] = new DocumentProperties { References = [body, pin] };
        var service = Service(db, store, folders);

        var session = await service.AnalyzeAsync([folders.Source]);
        var category = session.Catalog.FindByPath("Электротехника/Разъёмы")!.Id;
        foreach (var c in session.Candidates)
        {
            c.CategoryId = category;
            c.Manufacturer = "Harting";
        }
        store.FailWriteFor = "Вилка"; // одна деталь не внесётся — сборка без неё тоже

        var result = await service.CommitAsync(session);

        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.Failed.Count);
        Assert.False(File.Exists(Path.Combine(folders.Library, "Электротехника", "Разъёмы", "Harting", "Вилка X1 Harting.ipt")));
        Assert.False(File.Exists(Path.Combine(folders.Library, "Электротехника", "Разъёмы", "Harting", "Разъём X1 Harting.iam")));

        // Повторная попытка без сбоя: сборка ссылается на внесённые ранее и новые детали.
        store.FailWriteFor = null;
        var retry = await service.AnalyzeAsync([pin, asm]);
        foreach (var c in retry.Candidates)
        {
            c.CategoryId = category;
            c.Manufacturer = "Harting";
        }
        service.Validate(retry);
        Assert.All(retry.Candidates, c => Assert.True(c.CanImport, string.Join("; ", c.Issues.Select(i => i.Message))));
        Assert.Equal(2, (await service.CommitAsync(retry)).Added);

        var asmCopy = Path.Combine(folders.Library, "Электротехника", "Разъёмы", "Harting", "Разъём X1 Harting.iam");
        Assert.Equal(Path.Combine(folders.Library, "Электротехника", "Разъёмы", "Harting", "Вилка X1 Harting.ipt"), store.Relinked[asmCopy][pin]);
        await using var context = db.Database.CreateContext();
        Assert.Equal(2, await context.LibraryItemReferences.CountAsync());
    }
}
