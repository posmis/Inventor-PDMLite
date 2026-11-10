using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using PdmLite.Library.Backup;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class WindowsRestoreFactAttribute : FactAttribute
{
    public WindowsRestoreFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows directory sharing modes.";
    }
}

public class RestoreRollbackTests
{
    private static void Add(string folder, string name, string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), content);
    }

    [Fact]
    public void Rollback_PartiallyRemovedReplacement_DoesNotPreventReturnOfOriginalFolder()
    {
        using var folders = new TempFolders();
        var root = folders.Library;
        var previous = root + ".previous";
        var failed = root + ".failed";
        Add(previous, "original.ipt", "original");
        Add(root, "remaining.ipt", "partial replacement");
        var errors = RestoreRollback.Run(root, previous, failed, true, true, Directory.Move);
        Assert.Empty(errors);
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "original.ipt")));
        Assert.Equal("partial replacement", File.ReadAllText(Path.Combine(failed, "remaining.ipt")));
        Assert.False(Directory.Exists(previous));
    }

    [Fact]
    public void Rollback_NoOriginalFolder_ReturnsToAbsenceAndPreservesReplacement()
    {
        using var folders = new TempFolders();
        Add(folders.Library, "replacement.ipt", "new");
        var failed = folders.Library + ".failed";
        Assert.Empty(RestoreRollback.Run(folders.Library, folders.Library + ".previous", failed, false, true, Directory.Move));
        Assert.False(Directory.Exists(folders.Library));
        Assert.Equal("new", File.ReadAllText(Path.Combine(failed, "replacement.ipt")));
    }

    [Fact]
    public void Rollback_StagingWasNotInstalled_ReturnsOriginalWithoutMovingOrDeletingStaging()
    {
        using var folders = new TempFolders();
        var previous = folders.Library + ".previous";
        var staging = folders.Library + ".staging";
        Add(previous, "original.ipt", "old");
        Add(staging, "replacement.ipt", "new");
        Assert.Empty(RestoreRollback.Run(folders.Library, previous, staging, true, false, Directory.Move));
        Assert.Equal("old", File.ReadAllText(Path.Combine(folders.Library, "original.ipt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(staging, "replacement.ipt")));
    }

    [PostgresFact]
    public Task DatabaseFailure_AfterRealSwap_RollsBackDatabaseAndFiles() => DatabaseFailure("none");

    [PostgresFact]
    public Task DatabaseFailure_CannotMoveReplacement_PreservesBothFoldersAndReportsIncompleteRollback() => DatabaseFailure("evict");

    [PostgresFact]
    public Task DatabaseFailure_CannotReturnOriginal_PreservesBothFoldersAndReportsIncompleteRollback() => DatabaseFailure("return");

    [PostgresFact]
    public Task CancellationAfterSwap_DoesNotCancelFileRollback() => DatabaseFailure("cancel");

    [Fact]
    public void Rollback_MissingOriginal_IsReportedAndReplacementIsRetained()
    {
        using var folders = new TempFolders();
        Add(folders.Library, "replacement.ipt", "new");
        var failed = folders.Library + ".failed";
        var errors = RestoreRollback.Run(folders.Library, folders.Library + ".missing", failed, true, true, Directory.Move);
        Assert.NotEmpty(errors);
        Assert.Equal("new", File.ReadAllText(Path.Combine(failed, "replacement.ipt")));
    }

    [Fact]
    public void FailureDetails_ContainOriginalAndEveryRollbackErrorAndRecoveryPath()
    {
        var original = new InvalidOperationException("database failed");
        var moveError = new IOException("move failed");
        var returnError = new UnauthorizedAccessException("return denied");
        var error = new LibraryRestoreException(original, [moveError, returnError], "root", "previous", "failed", "before.zip");
        Assert.False(error.RollbackCompleted);
        Assert.Same(original, error.RestoreError);
        Assert.Equal([original, moveError, returnError], Assert.IsType<AggregateException>(error.InnerException).InnerExceptions);
        foreach (var text in new[] { "database failed", "move failed", "return denied", "root", "previous", "failed", "before.zip" })
            Assert.Contains(text, error.Message);
        Assert.DoesNotContain("Библиотека не изменена", error.Message);
    }

    [PostgresFact]
    public Task DatabaseFailure_CleanupFailsAfterReturn_OriginalFilesAndDatabaseStillAgree() => DatabaseFailure("cleanup");

    private static async Task DatabaseFailure(string fault)
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var backupFolder = Path.Combine(folders.Root, "backups");
        BackupOptions Options() => new(backupFolder, 10, 7);
        var importer = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        var items = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        var backup = new LibraryBackupService(db.Database, Storage, Options, NullLogger<LibraryBackupService>.Instance);
        async Task Import(string name, string bytes)
        {
            var path = folders.AddFile(name, bytes);
            Assert.Equal(1, (await importer.CommitAsync(await importer.AnalyzeAsync([path]))).Added);
        }
        await Import("Болт M8x20 ГОСТ 7798-70.ipt", "bolt");
        var archive = await backup.CreateAsync(BackupReason.Manual);
        await Import("Гайка M8 ГОСТ 5915-70.ipt", "nut");
        var catalog = await items.LoadCatalogAsync();
        var originalItems = await items.ListAsync(catalog);
        var originalFiles = Directory.GetFiles(folders.Library, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(folders.Library, p), File.ReadAllText);

        // Архив корректен; сбой возникает именно при INSERT после TRUNCATE в транзакции восстановления.
        await using (var context = db.Database.CreateContext())
            await context.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION public.reject_restore_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected restore database failure'; END $$;
                CREATE TRIGGER reject_restore BEFORE INSERT ON library.items
                FOR EACH ROW EXECUTE FUNCTION public.reject_restore_insert();
                """);

        using var stop = new CancellationTokenSource();
        var swapped = false;
        var service = new LibraryBackupService(db.Database, Storage, Options, NullLogger<LibraryBackupService>.Instance)
        {
            DeleteDirectory = path =>
            {
                if (fault == "cleanup" && path.Contains(".failed-restore-")) throw new UnauthorizedAccessException("blocked cleanup");
                Directory.Delete(path, recursive: true);
            },
            MoveDirectory = (from, to) =>
            {
                if (fault == "evict" && to.Contains(".failed-restore-")) throw new UnauthorizedAccessException("blocked replacement rename");
                if (fault == "return" && from.Contains(".before-restore-")) throw new IOException("blocked original return");
                Directory.Move(from, to);
                if (from.Contains(".restore-") && to == folders.Library)
                {
                    swapped = true;
                    if (fault == "cancel") stop.Cancel();
                    Assert.Single(Directory.GetFiles(folders.Library, "*", SearchOption.AllDirectories));
                }
            }
        };
        var error = await Assert.ThrowsAsync<LibraryRestoreException>(() => service.RestoreAsync(archive.Path, ct: stop.Token));
        Assert.True(swapped);
        if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error.RestoreError);
        else Assert.Contains("injected restore database failure", error.RestoreError.ToString());
        Assert.Equal(fault is "none" or "cancel" or "cleanup", error.RollbackCompleted);
        Assert.True(File.Exists(error.BackupPath));
        Assert.Equal(2, backup.Read(error.BackupPath)!.Manifest.Items);
        Assert.Equal(originalItems.Select(i => i.Id).Order(), (await items.ListAsync(catalog)).Select(i => i.Id).Order());
        await using (var context = db.Database.CreateContext())
            Assert.False(await context.LibraryLog.AnyAsync(l => l.Action == "restore"));

        var originalFolder = error.RollbackCompleted ? folders.Library : error.PreviousFolder;
        foreach (var (relative, content) in originalFiles)
        {
            var file = Path.Combine(originalFolder, relative);
            Assert.Equal(content, File.ReadAllText(file));
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        }
        if (error.RollbackCompleted)
        {
            Assert.Empty(error.RollbackErrors);
            Assert.False(Directory.Exists(error.PreviousFolder));
            Assert.Equal(fault == "cleanup", Directory.Exists(error.FailedFolder));
        }
        else
        {
            Assert.NotEmpty(error.RollbackErrors);
            Assert.Contains("Откат восстановления не завершён", error.Message);
            Assert.Contains(error.PreviousFolder, error.Message);
            Assert.Contains(error.BackupPath, error.Message);
            Assert.IsType<AggregateException>(error.InnerException);
            var replacement = fault == "evict" ? folders.Library : error.FailedFolder;
            Assert.Single(Directory.GetFiles(replacement, "*", SearchOption.AllDirectories));
        }
    }

    [WindowsRestoreFact]
    public void Windows_LockedReplacementDirectory_PreservesOriginalAndReportsFailure() => LockedDirectory(false);

    [WindowsRestoreFact]
    public void Windows_LockedOriginalDirectory_PreservesBothVersionsAndReportsFailure() => LockedDirectory(true);

    private static void LockedDirectory(bool lockPrevious)
    {
        using var folders = new TempFolders();
        var previous = folders.Library + ".previous";
        var failed = folders.Library + ".failed";
        Add(folders.Library, "new.ipt", "new");
        Add(previous, "old.ipt", "old");
        using var handle = CreateFileW(lockPrevious ? previous : folders.Library, 0,
            FileShare.Read | FileShare.Write, IntPtr.Zero, FileMode.Open, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var errors = RestoreRollback.Run(folders.Library, previous, failed, true, true, Directory.Move);
        Assert.NotEmpty(errors);
        Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "old.ipt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(lockPrevious ? failed : folders.Library, "new.ipt")));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, FileShare share,
        IntPtr security, FileMode creation, uint flags, IntPtr template);
}
