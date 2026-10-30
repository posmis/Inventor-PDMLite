using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Rules;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Утилита подготовки (Б2): правила замены, шаблоны, источник обозначения, список подготовки.</summary>
public class DesignationRuleTests
{
    private static DesignationRule Replace(string pattern, string replacement, bool regex = false, int order = 0, string? comment = null) =>
        new() { Pattern = pattern, Replacement = replacement, IsRegex = regex, SortOrder = order, Comment = comment };

    // --- Набор правил без базы ---

    [Fact]
    public void Replacements_ApplyInOrder_PlainAndRegex()
    {
        var set = new DesignationRuleSet(
        [
            Replace("Болт_", "Болт ", order: 1),
            Replace(@"(\d+)х(\d+)", "$1x$2", regex: true, order: 0, comment: "х → x между числами"),
            new DesignationRule { Pattern = "лишнее", Replacement = "", SortOrder = 2, Enabled = false },
        ]);

        var (result, applied) = set.ApplyReplacements("Болт_M10х25 лишнее");

        Assert.Equal("Болт M10x25 лишнее", result);
        Assert.Equal(["х → x между числами", "«Болт_» → «Болт »"], applied);
    }

    [Fact]
    public void Replacements_UnchangedText_ReportsNothing()
    {
        var set = new DesignationRuleSet([Replace("Винт", "Винт")]);
        var (result, applied) = set.ApplyReplacements("Болт M10");
        Assert.Equal("Болт M10", result);
        Assert.Empty(applied);
    }

    [Fact]
    public void InvalidRules_AreSkipped_AndReported()
    {
        var broken = Replace("(", "x", regex: true);
        var set = new DesignationRuleSet([broken, Replace("a", "b")]);

        Assert.Equal([broken], set.Invalid);
        Assert.Equal("b", set.ApplyReplacements("a").Result);
        Assert.Contains("регулярном", DesignationRuleSet.Validate(broken));
        Assert.NotNull(DesignationRuleSet.Validate(Replace("", "x")));
        Assert.Null(DesignationRuleSet.Validate(Replace("(", "x"))); // обычный текст — скобка допустима
    }

    // --- С базой ---

    private static LibraryImportService Service(TestDatabase db, FakePropertyStore store, TempFolders folders) =>
        new(db.Database, store, () => new LibraryStorage(folders.Library), NullLogger<LibraryImportService>.Instance);

    [PostgresFact]
    public async Task RuleService_SavesInListOrder_RejectsInvalid()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new DesignationRuleService(db.Database);

        await service.SaveAsync([Replace("b", "c", order: 9), Replace("a", "b", order: 5)]);
        var rules = await service.ListAsync();

        Assert.Equal(["b", "a"], rules.Select(r => r.Pattern));
        Assert.Equal("b", (await service.LoadSetAsync()).ApplyReplacements("a").Result); // сначала b→c, потом a→b
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync([Replace("(", "", regex: true)]));
        Assert.Equal(2, (await service.ListAsync()).Count); // неудачное сохранение ничего не меняет
    }

    [PostgresFact]
    public async Task Analyze_AppliesRules_DoesNotCheckFormat()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var good = folders.AddFile("Болт_M10х25 ГОСТ 7798-70.ipt", "a");
        var strict = folders.AddFile("Болт M12-6gx60.58 (S18) ГОСТ 7798-70.ipt", "b");
        await new DesignationRuleService(db.Database).SaveAsync([Replace("Болт_", "Болт ", comment: "подчёркивание после слова")]);

        var session = await Service(db, store, folders).AnalyzeAsync([good, strict]);

        var g = session.Candidates.Single(c => c.SourcePath == good);
        Assert.Equal("Болт M10x25 ГОСТ 7798-70", g.Designation);
        Assert.Equal(DesignationSource.FileName, g.Source);
        Assert.Equal(["подчёркивание после слова"], g.AppliedRules);
        Assert.Contains(g.Issues, i => i.Message.Contains("Применены правила"));

        // Обозначение по ГОСТ с размером «под ключ» берётся как есть, формат не проверяется.
        var b = session.Candidates.Single(c => c.SourcePath == strict);
        Assert.Equal("Болт M12-6gx60.58 (S18) ГОСТ 7798-70", b.Designation);
        Assert.Equal(ImportStatus.Ready, b.Status);
    }

    [PostgresFact]
    public async Task SetSource_TakesDesignationFromAnotherProperty_AndRecalculates()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var file = folders.AddFile("123.ipt", "a");
        var empty = folders.AddFile("Гайка M8 ГОСТ 5915-70.ipt", "b");
        store.Files[file] = new DocumentProperties { PartNumber = "123", Description = "Гайка М8-6Н.8 ГОСТ 5915-70" };
        var service = Service(db, store, folders);

        var session = await service.AnalyzeAsync([file, empty]);
        var c = session.Candidates.Single(x => x.SourcePath == file);
        Assert.Equal("123", c.Designation);
        Assert.Equal(LibraryItemKind.Purchased, c.Kind);

        var changed = service.SetSource(session, session.Candidates, DesignationSource.Description);

        Assert.Equal(1, changed); // у второго файла описание пустое — он не меняется
        Assert.Equal("Гайка M8-6H.8 ГОСТ 5915-70", c.Designation);
        Assert.Equal(DesignationSource.Description, c.Source);
        Assert.Equal("ГОСТ 5915-70", c.Standard);
        Assert.Equal(LibraryItemKind.Standard, c.Kind);
        Assert.Equal("Крепёж/Гайки", session.Catalog.Get(c.CategoryId)!.Path);
        Assert.Equal(DesignationSource.FileName, session.Candidates.Single(x => x.SourcePath == empty).Source);
        Assert.Throws<ArgumentException>(() => service.SetSource(session, session.Candidates, DesignationSource.Manual));
    }

    [PostgresFact]
    public async Task ReloadRules_ReappliesToAllButManual_ReplaceMarksManual()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var a = folders.AddFile("Болт_M10x25 ГОСТ 7798-70.ipt", "a");
        var b = folders.AddFile("Болт_M12x30 ГОСТ 7798-70.ipt", "b");
        var service = Service(db, store, folders);
        var session = await service.AnalyzeAsync([a, b]);
        var ca = session.Candidates.Single(c => c.SourcePath == a);
        var cb = session.Candidates.Single(c => c.SourcePath == b);

        Assert.Equal(1, service.ReplaceInDesignations(session, [cb], @"M(\d+)x30", "M$1x35", isRegex: true));
        Assert.Equal("Болт_M12x35 ГОСТ 7798-70", cb.Designation);
        Assert.Equal(DesignationSource.Manual, cb.Source);
        Assert.Equal(0, service.ReplaceInDesignations(session, [ca], "нет такого", "x", isRegex: false));

        await new DesignationRuleService(db.Database).SaveAsync([Replace("Болт_", "Болт ")]);
        await service.ReloadRulesAsync(session);

        Assert.Equal("Болт M10x25 ГОСТ 7798-70", ca.Designation);
        Assert.Equal("Болт_M12x35 ГОСТ 7798-70", cb.Designation); // ручная правка не тронута
    }

    [PostgresFact]
    public async Task List_SaveAndLoad_RestoresEdits_MissingFilesSkipped()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var a = folders.AddFile("Болт M10x25 ГОСТ 7798-70.ipt", "a");
        var b = folders.AddFile("Кнопка KB-1.ipt", "b");
        var gone = folders.AddFile("Удалённый.ipt", "c");
        var service = Service(db, store, folders);

        var session = await service.AnalyzeAsync([a, b, gone]);
        var cb = session.Candidates.Single(c => c.SourcePath == b);
        cb.Designation = "Кнопка KB-1 ABB";
        cb.Source = DesignationSource.Manual;
        cb.Manufacturer = "ABB";
        cb.ManualStandard = "ТУ 16-526.429";
        cb.CategoryId = session.Catalog.Leaves.First(c => c.Path == "Крепёж/Гайки").Id;
        session.Candidates.Single(c => c.SourcePath == a).Include = false;

        var listPath = Path.Combine(folders.Root, "подготовка" + ImportList.Extension);
        service.SaveList(session, listPath);
        File.Delete(gone);

        var loaded = await service.LoadListAsync(listPath);

        Assert.Equal(2, loaded.Candidates.Count);
        Assert.Contains(loaded.Skipped, s => s.Path == gone && s.Reason.Contains("не найден"));
        Assert.False(loaded.Candidates.Single(c => c.SourcePath == a).Include);
        var lb = loaded.Candidates.Single(c => c.SourcePath == b);
        Assert.Equal("Кнопка KB-1 ABB", lb.Designation);
        Assert.Equal(DesignationSource.Manual, lb.Source);
        Assert.Equal("ABB", lb.Manufacturer);
        Assert.Equal("ТУ 16-526.429", lb.ManualStandard);
        Assert.Equal("Крепёж/Гайки", loaded.Catalog.Get(lb.CategoryId)!.Path);
        Assert.Contains("Кнопка KB-1 ABB", File.ReadAllText(listPath)); // кириллица читаема

        File.WriteAllText(listPath, "не json");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadListAsync(listPath));
    }
}

