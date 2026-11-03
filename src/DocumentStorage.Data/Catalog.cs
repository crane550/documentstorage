using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Data;

public sealed class StorageRoot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsNetwork { get; set; }
    public bool IsOnline { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastScan { get; set; }
}

public sealed class CatalogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RootId { get; set; }
    public Guid? ParentId { get; set; }
    public string RelativePath { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public bool IsMissing { get; set; }
    public Guid? TrashBatchId { get; set; }
    public long Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string Instructions { get; set; } = "";
    public string ExtractedText { get; set; } = "";
    public string TextStatus { get; set; } = "Pending";
}

// Written before a disk move. A restart reconciles a prepared operation before scanning.
public sealed class FileOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RootId { get; set; }
    public Guid EntryId { get; set; }
    public string Kind { get; set; } = "";
    public string FromPath { get; set; } = "";
    public string ToPath { get; set; } = "";
    public Guid? TargetParentId { get; set; }
    public Guid? TrashBatchId { get; set; }
    public string State { get; set; } = "Prepared";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Error { get; set; }
}

public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    public DbSet<StorageRoot> Roots => Set<StorageRoot>();
    public DbSet<CatalogEntry> Entries => Set<CatalogEntry>();
    public DbSet<FileOperation> Operations => Set<FileOperation>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<StorageRoot>().HasIndex(x => x.Location).IsUnique();
        model.Entity<CatalogEntry>().HasIndex(x => new { x.RootId, x.RelativePath }).IsUnique();
        model.Entity<CatalogEntry>().HasIndex(x => x.ParentId);
        model.Entity<CatalogEntry>().HasOne<StorageRoot>().WithMany().HasForeignKey(x => x.RootId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FileOperation>().HasIndex(x => new { x.RootId, x.State });
    }
}
