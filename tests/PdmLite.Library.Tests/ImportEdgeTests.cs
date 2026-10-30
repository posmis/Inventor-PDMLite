using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public class ImportEdgeTests
{
    private static LibraryImportService Service(TestDatabase db, IDocumentPropertyStore store, TempFolders folders) =>
        new(db.Database, store, () => new LibraryStorage(folders.Library), NullLogger<LibraryImportService>.Instance);

    [PostgresFact]
    public async Task Analyze_SkipsNonInventorFiles_AndFilesAlreadyInLibrary()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var part = folders.AddFile("Болт M8-6gx20.109 ГОСТ 7798-70.ipt", "b");
        var pdf = folders.AddFile("Чертёж.pdf", "pdf");
        Directory.CreateDirectory(folders.Library);
        var inLibrary = Path.Combine(folders.Library, "Гайка M8 ГОСТ 5915-70.ipt");
        File.WriteAllText(inLibrary, "n");

        var session = await Service(db, new FakePropertyStore(), folders).AnalyzeAsync([part, pdf, inLibrary]);

        Assert.Single(session.Candidates);
        Assert.Contains(session.Skipped, s => s.Path == pdf && s.Reason.Contains("Не деталь"));
        Assert.Contains(session.Skipped, s => s.Path == inLibrary && s.Reason.Contains("папке библиотеки"));
    }

    [PostgresFact]
    public async Task Analyze_LockedFile_IsReportedButDoesNotStopOthers()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var locked = folders.AddFile("Болт M8-6gx20.109 ГОСТ 7798-70.ipt", "b");
        var free = folders.AddFile("Гайка M8-6H.10 ГОСТ 5915-70.ipt", "n");

        Session session;
        await using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            session = new Session(await Service(db, new FakePropertyStore(), folders).AnalyzeAsync([folders.Source]));

        var lockedCandidate = session.Value.Candidates.Single(c => c.SourcePath == locked);
        Assert.Equal(ImportStatus.Error, lockedCandidate.Status);
        Assert.Contains(lockedCandidate.Issues, i => i.Message.Contains("не прочитан"));
        Assert.True(session.Value.Candidates.Single(c => c.SourcePath == free).CanImport);
    }

    private sealed record Session(ImportSession Value);

    [PostgresFact]
    public async Task Analyze_WithoutPropertyStore_BlocksImportWithReason()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        folders.AddFile("Болт M8-6gx20.109 ГОСТ 7798-70.ipt", "b");

        var session = await Service(db, new UnavailablePropertyStore("нет Inventor View."), folders).AnalyzeAsync([folders.Source]);

        var c = Assert.Single(session.Candidates);
        Assert.False(c.CanImport);
        Assert.Contains(c.Issues, i => i.Message.Contains("нет Inventor View"));
        Assert.Equal(0, (await Service(db, new UnavailablePropertyStore("нет Inventor View."), folders).CommitAsync(session)).Added);
    }

    [PostgresFact]
    public async Task Validate_ContentCenterIsInfo_AndPropertyMismatchIsWarning()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var file = folders.AddFile("Заклепка 2 2x4x2,8 ГОСТ 12641-80.ipt", "r");
        store.Files[file] = new DocumentProperties
        {
            PartNumber = "Заклепка 2 2x4 ГОСТ 12641-80",
            Title = "Заклепка 2 2x4 ГОСТ 12641-80",
            IsContentCenter = true,
        };

        var c = Assert.Single((await Service(db, store, folders).AnalyzeAsync([file])).Candidates);

        Assert.Equal("Заклепка 2 2x4 ГОСТ 12641-80", c.Designation); // обозначение — из свойств, не из имени файла
        Assert.Equal(ImportStatus.Ready, c.Status); // метка Content Center снимется при внесении — это справка
        Assert.Contains(c.Issues, i => i.Severity == ImportSeverity.Info && i.Message.Contains("Content Center"));
        Assert.Contains(c.Issues, i => i.Severity == ImportSeverity.Info && i.Message.Contains("Имя файла"));
    }

    [PostgresFact]
    public async Task Validate_KindSwitch_RecomputesStandardAndPath()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var service = Service(db, new FakePropertyStore(), folders);
        var file = folders.AddFile("Кабельный ввод SKINTOP M20 Lapp.ipt", "k");

        var session = await service.AnalyzeAsync([file]);
        var c = Assert.Single(session.Candidates);
        Assert.Equal(LibraryItemKind.Purchased, c.Kind);
        Assert.Equal("Электротехника/Кабельные вводы", session.Catalog.Get(c.CategoryId)!.Path);

        c.Manufacturer = "Lapp";
        service.Validate(session);
        Assert.Equal("Электротехника/Кабельные вводы/Lapp/Кабельный ввод SKINTOP M20 Lapp.ipt", c.TargetRelativePath);

        c.Kind = LibraryItemKind.Standard; // стандарта в обозначении нет
        service.Validate(session);
        Assert.Contains(c.Issues, i => i.Message.Contains("не найден стандарт"));
    }

    [PostgresFact]
    public async Task Validate_ExcludedCandidate_HasNoIssues_AndDoesNotCountAsDuplicate()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var service = Service(db, new FakePropertyStore(), folders);
        folders.AddFile("a/Гайка M8-6H.10 ГОСТ 5915-70.ipt", "1");
        folders.AddFile("b/Гайка M8-6H.10 ГОСТ 5915-70.ipt", "2");

        var session = await service.AnalyzeAsync([folders.Source]);
        session.Candidates[1].Include = false;
        service.Validate(session);

        Assert.Equal(ImportStatus.Excluded, session.Candidates[1].Status);
        Assert.Empty(session.Candidates[1].Issues);
        Assert.True(session.Candidates[0].CanImport);
    }

    [PostgresFact]
    public async Task Validate_AssemblyWithComponentOutsideList_IsError()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var asm = folders.AddFile("Разъём X1 Harting.iam", "asm");
        store.Files[asm] = new DocumentProperties { References = [Path.Combine(folders.Root, "elsewhere", "Корпус.ipt")] };

        var c = Assert.Single((await Service(db, store, folders).AnalyzeAsync([asm])).Candidates);
        Assert.Contains(c.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains("Корпус.ipt"));
    }

    [PostgresFact]
    public async Task Commit_Twice_DoesNotDuplicate()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var service = Service(db, new FakePropertyStore(), folders);
        folders.AddFile("Гайка M8-6H.10 ГОСТ 5915-70.ipt", "n");

        var session = await service.AnalyzeAsync([folders.Source]);
        Assert.Equal(1, (await service.CommitAsync(session)).Added);
        var second = await service.CommitAsync(session); // тот же сеанс повторно

        Assert.Equal(0, second.Added);
        Assert.Empty(second.Failed);
        Assert.Contains(session.Candidates[0].Issues, i => i.Message.Contains("уже есть"));
        await using var context = db.Database.CreateContext();
        Assert.Equal(1, await context.LibraryItems.CountAsync());
    }

    [PostgresFact]
    public async Task Commit_FailedPropertyWrite_LeavesNoFileAndNoRecord()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore { FailWriteFor = "Гайка" };
        var service = Service(db, store, folders);
        folders.AddFile("Гайка M8-6H.10 ГОСТ 5915-70.ipt", "n");

        var result = await service.CommitAsync(await service.AnalyzeAsync([folders.Source]));

        Assert.Equal(0, result.Added);
        Assert.Contains("повреждён", Assert.Single(result.Failed).Error);
        Assert.Empty(Directory.EnumerateFiles(folders.Library, "*", SearchOption.AllDirectories));
        await using var context = db.Database.CreateContext();
        Assert.Equal(0, await context.LibraryItems.CountAsync());
    }

    [PostgresFact]
    public async Task Analyze_TakesKindFromSpecSectionInFile()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        // В обозначении есть ГОСТ, но в файле записано «прочее изделие» — побеждает файл.
        var screw = folders.AddFile("Винт ГОСТ 11738-84 M8x25.ipt", "s");
        store.Files[screw] = new DocumentProperties { Custom = new Dictionary<string, string> { ["SPEC_Section"] = "OtherProducts" } };
        var nut = folders.AddFile("Гайка M8 особая.ipt", "n");
        store.Files[nut] = new DocumentProperties { Custom = new Dictionary<string, string> { ["SPEC_Section"] = " standardproducts " } };
        var plain = folders.AddFile("Шайба 8 ГОСТ 11371-78.ipt", "w");

        var session = await Service(db, store, folders).AnalyzeAsync([folders.Source]);

        var screwCandidate = session.Candidates.Single(c => c.SourcePath == screw);
        Assert.Equal(LibraryItemKind.Purchased, screwCandidate.Kind);
        Assert.Contains(screwCandidate.Issues, i => i.Message.Contains("SPEC_Section"));
        Assert.Equal(LibraryItemKind.Standard, session.Candidates.Single(c => c.SourcePath == nut).Kind);
        var plainCandidate = session.Candidates.Single(c => c.SourcePath == plain);
        Assert.Equal(LibraryItemKind.Standard, plainCandidate.Kind); // свойства нет — тип по стандарту в обозначении
        Assert.False(plainCandidate.KindFromFile);
    }

    [PostgresFact]
    public async Task Commit_RemovesContentCenterLink()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var service = Service(db, store, folders);
        var bolt = folders.AddFile("Болт M6-6gx20.88.016 ГОСТ 7798-70.ipt", "cc");
        store.Files[bolt] = new DocumentProperties { PartNumber = "Болт M6-6gx20.88.016 ГОСТ 7798-70", IsContentCenter = true };

        Assert.Equal(1, (await service.CommitAsync(await service.AnalyzeAsync([bolt]))).Added);

        var target = store.Written.Keys.Single();
        Assert.True(store.Written[target].RemoveContentCenterLink);
        Assert.False(store.Files[target].IsContentCenter);
        Assert.True(store.Files[bolt].IsContentCenter); // исходный файл не меняется
    }
}
