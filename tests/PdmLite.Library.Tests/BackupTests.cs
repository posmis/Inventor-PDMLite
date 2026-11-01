using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Rules;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Резервные копии библиотеки (Б6).</summary>
public class BackupTests
{
    private sealed record Services(LibraryImportService Import, LibraryItemService Items, LibraryBackupService Backup, string BackupFolder);

    private static Services Create(TestDatabase db, FakePropertyStore store, TempFolders folders, int keep = 10, int interval = 7)
    {
        LibraryStorage Storage() => new(folders.Library);
        var backups = Path.Combine(folders.Root, "Backups");
        return new Services(
            new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance),
            new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance),
            new LibraryBackupService(db.Database, Storage, () => new BackupOptions(backups, keep, interval), NullLogger<LibraryBackupService>.Instance),
            backups);
    }

    private static async Task ImportAsync(Services s, params string[] files) =>
        Assert.Equal(files.Length, (await s.Import.CommitAsync(await s.Import.AnalyzeAsync(files))).Added);

    [PostgresFact]
    public async Task Create_WritesManifestDataAndFiles()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var s = Create(db, new FakePropertyStore(), folders);
        await ImportAsync(s, folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt"), folders.AddFile("Гайка M8 ГОСТ 5915-70.ipt", "nut"));

        var backup = await s.Backup.CreateAsync(BackupReason.Manual);

        Assert.Contains("вручную", backup.FileName);
        Assert.Equal(2, backup.Manifest.Items);
        Assert.Equal(2, backup.Manifest.Files);
        Assert.False(backup.IsAutomatic);
        using var zip = ZipFile.OpenRead(backup.Path);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("data/library.json"));
        Assert.Contains(zip.Entries, e => e.FullName == "files/Крепёж/Болты/ГОСТ 7798-70/Болт M8x20 ГОСТ 7798-70.ipt");
        Assert.Equal([backup.Path], s.Backup.List().Select(b => b.Path));
        Assert.False(File.Exists(backup.Path + ".part"));
    }

    [PostgresFact]
    public async Task Restore_ReturnsDatabaseAndFiles_AndKeepsCopyOfCurrentState()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var s = Create(db, store, folders);
        await ImportAsync(s, folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt"));
        await new DesignationRuleService(db.Database).SaveAsync([new DesignationRule { Pattern = "Болт_", Replacement = "Болт " }]);
        var backup = await s.Backup.CreateAsync(BackupReason.Manual);

        // После копии: болт удалили, внесли гайку, в папку попал посторонний файл.
        var catalog = await s.Items.LoadCatalogAsync();
        var bolt = Assert.Single(await s.Items.ListAsync(catalog));
        await s.Items.DeleteAsync(bolt.Id);
        await ImportAsync(s, folders.AddFile("Гайка M8 ГОСТ 5915-70.ipt", "nut"));
        File.WriteAllText(Path.Combine(folders.Library, "мусор.txt"), "x");

        var plan = await s.Backup.PlanRestoreAsync(backup.Path);
        Assert.Equal(["Болт M8x20 ГОСТ 7798-70"], plan.Added);
        Assert.Equal(["Гайка M8 ГОСТ 5915-70"], plan.Removed);
        Assert.False(plan.IsDamaged);

        var before = await s.Backup.RestoreAsync(backup.Path);

        Assert.Equal(BackupReason.BeforeRestore, before.Manifest.Reason);
        var items = await s.Items.ListAsync(await s.Items.LoadCatalogAsync());
        var restored = Assert.Single(items);
        Assert.Equal(bolt.Id, restored.Id);
        var boltFile = folders.InLibrary(restored.RelativePath);
        Assert.Equal("bolt*", File.ReadAllText(boltFile)); // «*» — запись свойств при внесении
        Assert.True(File.GetAttributes(boltFile).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.Exists(Path.Combine(folders.Library, "мусор.txt")));
        Assert.Single(await new DesignationRuleService(db.Database).ListAsync());
        Assert.DoesNotContain(Directory.GetDirectories(folders.Root), d => Path.GetFileName(d).StartsWith("Library.")); // временные папки убраны

        // Счётчики Id продолжаются после восстановленных: новые записи вносятся без конфликтов.
        await ImportAsync(s, folders.AddFile("Шайба 8 ГОСТ 11371-78.ipt", "washer"));
        await new DesignationRuleService(db.Database).SaveAsync([new DesignationRule { Pattern = "a", Replacement = "b" }]);
        await using var context = db.Database.CreateContext();
        Assert.Contains(context.LibraryLog, l => l.Action == "restore");
    }

    [PostgresFact]
    public async Task Restore_FailingDatabase_LeavesLibraryAsItWas()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var s = Create(db, new FakePropertyStore(), folders);
        await ImportAsync(s, folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt"));
        var backup = await s.Backup.CreateAsync(BackupReason.Manual);
        await ImportAsync(s, folders.AddFile("Гайка M8 ГОСТ 5915-70.ipt", "nut"));

        // Портим данные копии: два компонента с одним Id — вставка в базу упадёт.
        var broken = Path.Combine(folders.Root, "broken.zip");
        File.Copy(backup.Path, broken);
        using (var zip = ZipFile.Open(broken, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("data/library.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open()))
                json = reader.ReadToEnd();
            entry.Delete();
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var items = doc["Items"]!.AsArray();
            items.Add(items[0]!.DeepClone());
            using var writer = new StreamWriter(zip.CreateEntry("data/library.json").Open());
            writer.Write(doc.ToJsonString());
        }

        await Assert.ThrowsAnyAsync<Exception>(() => s.Backup.RestoreAsync(broken));

        Assert.Equal(2, (await s.Items.ListAsync(await s.Items.LoadCatalogAsync())).Count);
        Assert.True(File.Exists(Path.Combine(folders.Library, "Крепёж", "Гайки", "ГОСТ 5915-70", "Гайка M8 ГОСТ 5915-70.ipt")));
        Assert.DoesNotContain(Directory.GetDirectories(folders.Root), d => Path.GetFileName(d).StartsWith("Library."));
    }

    [PostgresFact]
    public async Task Prune_KeepsLastAutomatic_NeverManual_AndScheduleIsDue()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var s = Create(db, new FakePropertyStore(), folders, keep: 2);
        Assert.True(s.Backup.IsScheduledDue()); // копий ещё нет

        var manual = await s.Backup.CreateAsync(BackupReason.Manual);
        for (var i = 0; i < 3; i++)
            await s.Backup.CreateAsync(BackupReason.Scheduled);

        var list = s.Backup.List();
        Assert.Equal(2, list.Count(b => b.IsAutomatic));
        Assert.Contains(list, b => b.Path == manual.Path);
        Assert.False(s.Backup.IsScheduledDue());
        Assert.False(Create(db, new FakePropertyStore(), folders, interval: 0).Backup.IsScheduledDue()); // расписание выключено
    }

    [PostgresFact]
    public async Task NotABackup_OrDamaged_IsRejected()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var s = Create(db, new FakePropertyStore(), folders);
        await ImportAsync(s, folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt"));

        var notBackup = Path.Combine(folders.Root, "other.zip");
        using (var zip = ZipFile.Open(notBackup, ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Backup.PlanRestoreAsync(notBackup));

        var backup = await s.Backup.CreateAsync(BackupReason.Manual);
        using (var zip = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
            zip.Entries.Single(e => e.FullName.EndsWith(".ipt")).Delete();
        var plan = await s.Backup.PlanRestoreAsync(backup.Path);
        Assert.True(plan.IsDamaged);
        Assert.Contains("нет файла", plan.MissingFiles[0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Backup.RestoreAsync(backup.Path));

        // Файл в архиве подменён — контрольная сумма не совпадает.
        var second = await s.Backup.CreateAsync(BackupReason.Manual);
        using (var zip = ZipFile.Open(second.Path, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.Single(e => e.FullName.EndsWith(".ipt"));
            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("другое содержимое");
        }
        var tampered = await s.Backup.PlanRestoreAsync(second.Path);
        Assert.True(tampered.IsDamaged);
        Assert.Contains("повреждён", tampered.MissingFiles[0]);

        // Файл изменён вне PdmLite: снимок базы и файла не согласован, копию создавать нельзя.
        var item = Assert.Single(await s.Items.ListAsync(await s.Items.LoadCatalogAsync()));
        var file = folders.InLibrary(item.RelativePath);
        File.SetAttributes(file, FileAttributes.Normal);
        File.AppendAllText(file, "изменён в Inventor");
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Backup.CreateAsync(BackupReason.Manual));
    }
}
