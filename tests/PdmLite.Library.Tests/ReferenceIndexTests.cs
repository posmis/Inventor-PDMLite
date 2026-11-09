using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Index;

namespace PdmLite.Library.Tests;

/// <summary>Индекс ссылок проекта: состав, где используется, битые ссылки, повторное сканирование.</summary>
public class ReferenceIndexTests
{
    private sealed record Project(string Root, string Part, string Missing, string Asm, string Drawing, string Old);

    /// <summary>Проект: сборка из детали и пропавшей детали, чертёж сборки, резервная копия Inventor.</summary>
    private static Project Build(TempFolders folders, FakePropertyStore store)
    {
        var part = folders.AddFile("Детали/АБВГ.001_Пластина.ipt", "p");
        var missing = Path.Combine(folders.Source, "Детали", "Удалённая.ipt");
        var asm = folders.AddFile("АБВГ.100_Узел.iam", "a");
        var drawing = folders.AddFile("Чертежи/АБВГ.100СБ_Узел.dwg", "d");
        var old = folders.AddFile("OldVersions/АБВГ.100_Узел.0001.iam", "o");
        store.Files[part] = new DocumentProperties { PartNumber = "АБВГ.001", Title = "Пластина" };
        store.Files[asm] = new DocumentProperties { PartNumber = "АБВГ.100", Title = "Узел", References = [part, missing, part] };
        store.Files[drawing] = new DocumentProperties { References = [asm] };
        store.Files[old] = new DocumentProperties { References = [part] };
        return new Project(folders.Source, part, missing, asm, drawing, old);
    }

    private static ReferenceIndexService Service(TestDatabase db, FakePropertyStore store) =>
        new(db.Database, store, NullLogger<ReferenceIndexService>.Instance);

    [PostgresFact]
    public async Task Scan_WhereUsed_Components_Broken()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var p = Build(folders, store);
        var index = Service(db, store);
        Assert.False(await index.IsIndexedAsync(p.Root));

        var result = await index.ScanAsync(p.Root);

        Assert.Equal(3, result.Files); // OldVersions пропущена
        Assert.Equal(3, result.Read);
        Assert.True(await index.IsIndexedAsync(p.Root));

        var direct = await index.WhereUsedAsync(p.Part, allLevels: false);
        Assert.Equal([p.Asm], direct.Select(d => d.Path));
        Assert.Equal("АБВГ.100", direct[0].PartNumber);

        var all = await index.WhereUsedAsync(p.Part, allLevels: true);
        Assert.Equal([(p.Asm, 1), (p.Drawing, 2)], all.Select(d => (d.Path, d.Level)));
        Assert.Equal(DocumentKind.Drawing, all[1].Kind);

        var components = await index.ComponentsAsync(p.Asm);
        Assert.Equal(2, components.Count); // повтор ссылки учтён один раз
        Assert.False(components[0].Exists); // ненайденные — первыми
        Assert.Equal(p.Missing, components[0].Path);
        Assert.Equal("Пластина", components[1].Title);

        var broken = Assert.Single(await index.BrokenReferencesAsync(p.Root));
        Assert.Equal((p.Asm, p.Missing), (broken.Document, broken.Target));
        Assert.Empty(await index.WhereUsedAsync(p.Drawing, allLevels: true));
    }

    [PostgresFact]
    public async Task Rescan_ReadsOnlyChanged_RemovesDeleted_RecordsErrors()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var p = Build(folders, store);
        var index = Service(db, store);
        await index.ScanAsync(p.Root);

        Assert.Equal(0, (await index.ScanAsync(p.Root)).Read); // ничего не менялось

        // Сборка больше не ссылается на деталь; чертёж удалён; появился нечитаемый DWG.
        store.Files[p.Asm] = new DocumentProperties { References = [] };
        File.AppendAllText(p.Asm, "изменена");
        File.Delete(p.Drawing);
        var acad = folders.AddFile("Чертежи/Схема AutoCAD.dwg", "acad");
        store.FailReadFor = "AutoCAD";

        var result = await index.ScanAsync(p.Root);

        Assert.Equal(2, result.Read); // сборка и новый DWG
        Assert.Equal(1, result.Removed);
        Assert.Contains(result.Errors, e => e.Contains("AutoCAD"));
        Assert.Empty(await index.WhereUsedAsync(p.Part, allLevels: true));
        Assert.Empty(await index.BrokenReferencesAsync(p.Root));
        Assert.Null(await index.ScannedAtAsync(acad)); // ошибка не является подтверждённой индексацией
        Assert.NotNull(await index.ReadErrorAsync(acad));
        var failedRetry = await index.ScanAsync(p.Root);
        Assert.Equal(1, failedRetry.Read); Assert.Single(failedRetry.Errors);
        store.FailReadFor = null;
        var recovered = await index.ScanAsync(p.Root);
        Assert.Equal(1, recovered.Read); Assert.Empty(recovered.Errors);
        Assert.NotNull(await index.ScannedAtAsync(acad)); Assert.Null(await index.ReadErrorAsync(acad));
    }

    [Fact]
    public void Key_IgnoresCaseAndSeparators() =>
        Assert.Equal(ReferenceIndexService.Key(Path.Combine(Path.GetTempPath(), "A", "b.ipt")),
            ReferenceIndexService.Key(Path.Combine(Path.GetTempPath(), "a", "B.IPT") + Path.DirectorySeparatorChar));

    [PostgresFact]
    public async Task FailedRead_PreservesReferences_AndRetriesWithoutFileChange()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var p = Build(folders, store);
        var index = Service(db, store);
        await index.ScanAsync(p.Root);
        File.AppendAllText(p.Asm, "changed");
        store.FailReadFor = Path.GetFileName(p.Asm);
        Assert.NotEmpty((await index.ScanAsync(p.Root)).Errors);
        Assert.Contains(await index.WhereUsedAsync(p.Part, false), d => d.Path == p.Asm);
        store.FailReadFor = null;
        Assert.Equal(1, (await index.ScanAsync(p.Root)).Read);
        Assert.Contains(await index.WhereUsedAsync(p.Part, false), d => d.Path == p.Asm);
    }

    [PostgresFact]
    public async Task MissingComponents_OfOneAssembly_AllLevels_NamesOnce()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var board = Path.Combine(folders.Source, "Электроника", "Плата.ipt"); // перенесли — файла нет
        var connector = Path.Combine(folders.Source, "Электроника", "Разъём X1.ipt");
        var block = folders.AddFile("Блок.iam", "b");
        var top = folders.AddFile("Изделие.iam", "t");
        var other = folders.AddFile("Другое.iam", "o");
        store.Files[block] = new DocumentProperties { References = [board, connector] };
        store.Files[top] = new DocumentProperties { References = [block, board] }; // плата и в изделии, и в блоке
        store.Files[other] = new DocumentProperties { References = [Path.Combine(folders.Source, "Чужое.ipt")] };
        var index = Service(db, store);
        await index.ScanAsync(folders.Source);

        var missing = await index.MissingComponentsAsync(top);

        Assert.Equal(["Плата.ipt", "Разъём X1.ipt"], missing.Select(m => m.Name)); // только для этой сборки, без «Чужое.ipt»
        Assert.Equal([block, top], missing[0].UsedIn);
        Assert.Equal(3, (await index.MissingFilesAsync(folders.Source)).Count); // по проекту — каждый файл один раз
    }
}
