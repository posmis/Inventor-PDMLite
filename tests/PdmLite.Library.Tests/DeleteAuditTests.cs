using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data.Library;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public class DeleteAuditTests
{
    [PostgresFact]
    public async Task Delete_DatabaseFailureRestoresFileAndRecord()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid(); var full = Path.Combine(folders.Library, "a.ipt");
        Directory.CreateDirectory(folders.Library); File.WriteAllText(full, "original"); LibraryStorage.MakeReadOnly(full);
        await using (var context = db.Database.CreateContext())
        {
            var category = new LibraryCategory { Name = "Test" }; context.LibraryCategories.Add(category); await context.SaveChangesAsync();
            context.LibraryItems.Add(new LibraryItem { Id = id, CategoryId = category.Id, Designation = "A", DesignationKey = "A", RelativePath = "a.ipt", FileHash = "hash", CreatedBy = "test", ModifiedBy = "test", CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync("CREATE FUNCTION library.reject_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test delete failure'; END $$");
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_delete BEFORE DELETE ON library.items FOR EACH ROW EXECUTE FUNCTION library.reject_delete()");
        }
        var service = new LibraryItemService(db.Database, new FakePropertyStore(), () => new LibraryStorage(folders.Library), NullLogger<LibraryItemService>.Instance);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.DeleteAsync(id));
        Assert.Equal("original", File.ReadAllText(full)); Assert.True(File.GetAttributes(full).HasFlag(FileAttributes.ReadOnly));
        Assert.Single(Directory.GetFiles(folders.Library));
        await using var after = db.Database.CreateContext(); Assert.True(await after.LibraryItems.AnyAsync(i => i.Id == id));
        Assert.False(await after.LibraryLog.AnyAsync(i => i.Action == "delete"));
    }
    [PostgresFact]
    public async Task Delete_FileCannotBeMoved_DoesNotDeleteDatabaseRecord()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid(); var full = Path.Combine(folders.Library, "a.ipt");
        Directory.CreateDirectory(folders.Library); File.WriteAllText(full, "original");
        await using (var context = db.Database.CreateContext())
        {
            var category = new LibraryCategory { Name = "Test" }; context.LibraryCategories.Add(category); await context.SaveChangesAsync();
            context.LibraryItems.Add(new LibraryItem { Id = id, CategoryId = category.Id, Designation = "A", DesignationKey = "A", RelativePath = "a.ipt", FileHash = "hash", CreatedBy = "test", ModifiedBy = "test", CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }
        var service = new LibraryItemService(db.Database, new FakePropertyStore(), () => new LibraryStorage(folders.Library), NullLogger<LibraryItemService>.Instance);
        FileStream? locked = null;
        UnixFileMode mode = default;
        try
        {
            if (OperatingSystem.IsWindows()) locked = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            else { mode = File.GetUnixFileMode(folders.Library); File.SetUnixFileMode(folders.Library, UnixFileMode.UserRead | UnixFileMode.UserExecute); }
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => service.DeleteAsync(id));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        finally
        {
            locked?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folders.Library, mode);
        }
        Assert.Equal("original", File.ReadAllText(full));
        await using var after = db.Database.CreateContext();
        Assert.True(await after.LibraryItems.AnyAsync(i => i.Id == id));
        Assert.False(await after.LibraryLog.AnyAsync(i => i.Action == "delete"));
    }

}
