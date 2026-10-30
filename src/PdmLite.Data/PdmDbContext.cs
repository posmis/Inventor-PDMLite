using Microsoft.EntityFrameworkCore;
using PdmLite.Data.Library;

namespace PdmLite.Data;

/// <summary>База данных PdmLite. Таблицы библиотеки — в схеме <c>library</c>.</summary>
public sealed class PdmDbContext(DbContextOptions<PdmDbContext> options) : DbContext(options)
{
    public const string LibrarySchema = "library";

    public DbSet<LibraryCategory> LibraryCategories => Set<LibraryCategory>();
    public DbSet<LibraryItem> LibraryItems => Set<LibraryItem>();
    public DbSet<LibraryItemVersion> LibraryItemVersions => Set<LibraryItemVersion>();
    public DbSet<LibraryItemProperty> LibraryItemProperties => Set<LibraryItemProperty>();
    public DbSet<LibraryItemReference> LibraryItemReferences => Set<LibraryItemReference>();
    public DbSet<LibraryCategoryRule> LibraryCategoryRules => Set<LibraryCategoryRule>();
    public DbSet<LibraryLogEntry> LibraryLog => Set<LibraryLogEntry>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LibraryCategory>(e =>
        {
            e.ToTable("categories", LibrarySchema);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ParentId, x.Name }).IsUnique();
        });

        model.Entity<LibraryItem>(e =>
        {
            e.ToTable("items", LibrarySchema);
            e.Property(x => x.Designation).HasMaxLength(400);
            e.Property(x => x.DesignationKey).HasMaxLength(400);
            e.Property(x => x.Standard).HasMaxLength(100);
            e.Property(x => x.Manufacturer).HasMaxLength(200);
            e.Property(x => x.RelativePath).HasMaxLength(1000);
            e.Property(x => x.FileHash).HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.ModifiedBy).HasMaxLength(100);
            e.HasIndex(x => x.DesignationKey).IsUnique();
            e.HasIndex(x => x.RelativePath).IsUnique();
            e.Property(x => x.SourceHash).HasMaxLength(64);
            e.HasIndex(x => x.FileHash);
            e.HasIndex(x => x.SourceHash);
            e.HasIndex(x => x.Standard);
            e.HasIndex(x => x.Manufacturer);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Versions).WithOne(x => x.Item).HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Properties).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.References).WithOne().HasForeignKey(x => x.AssemblyId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LibraryItemVersion>(e =>
        {
            e.ToTable("item_versions", LibrarySchema);
            e.Property(x => x.RelativePath).HasMaxLength(1000);
            e.Property(x => x.FileHash).HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.HasIndex(x => new { x.ItemId, x.Version }).IsUnique();
        });

        model.Entity<LibraryItemProperty>(e =>
        {
            e.ToTable("item_properties", LibrarySchema);
            e.HasKey(x => new { x.ItemId, x.Name });
            e.Property(x => x.Name).HasMaxLength(200);
        });

        model.Entity<LibraryItemReference>(e =>
        {
            e.ToTable("item_references", LibrarySchema);
            e.HasKey(x => new { x.AssemblyId, x.ComponentId });
            e.HasOne<LibraryItem>().WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LibraryCategoryRule>(e =>
        {
            e.ToTable("category_rules", LibrarySchema);
            e.Property(x => x.Pattern).HasMaxLength(200);
            e.HasIndex(x => new { x.Kind, x.Pattern }).IsUnique();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LibraryLogEntry>(e =>
        {
            e.ToTable("log", LibrarySchema);
            e.Property(x => x.User).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.ItemId);
        });
    }
}
