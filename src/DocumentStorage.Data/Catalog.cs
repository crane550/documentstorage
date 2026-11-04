using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

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
    public bool IsIntake { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string ContentHash { get; set; } = "";
    public string Readiness { get; set; } = "Not queued";
}

public sealed class DocumentReview
{
    public Guid EntryId { get; set; }
    public string DocumentType { get; set; } = "";
    public string FactsJson { get; set; } = "{}";
    public string ContentHash { get; set; } = "";
    public bool Reviewed { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed class FilingRuleDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public Guid DestinationFolderId { get; set; }
    public string MatchJson { get; set; } = "{}";
    public string FilenameTemplate { get; set; } = "";
    public string RequiredFactsJson { get; set; } = "[]";
    public bool Enabled { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed class FilingRuleRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public int Revision { get; set; }
    public string SnapshotJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class FilingPreview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EntryId { get; set; }
    public Guid RuleId { get; set; }
    public int RuleRevision { get; set; }
    public int ReviewRevision { get; set; }
    public Guid DestinationFolderId { get; set; }
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string Filename { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string State { get; set; } = "Pending";
    public Guid? OperationId { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
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
    public Guid? FilingPreviewId { get; set; }
}

public sealed class LibraryUser : IdentityUser;

public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : IdentityDbContext<LibraryUser>(options)
{
    public DbSet<StorageRoot> Roots => Set<StorageRoot>();
    public DbSet<CatalogEntry> Entries => Set<CatalogEntry>();
    public DbSet<FileOperation> Operations => Set<FileOperation>();
    public DbSet<DocumentReview> Reviews => Set<DocumentReview>();
    public DbSet<FilingRuleDefinition> FilingRules => Set<FilingRuleDefinition>();
    public DbSet<FilingRuleRevision> RuleRevisions => Set<FilingRuleRevision>();
    public DbSet<FilingPreview> FilingPreviews => Set<FilingPreview>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<StorageRoot>().HasIndex(x => x.Location).IsUnique();
        model.Entity<CatalogEntry>().HasIndex(x => new { x.RootId, x.RelativePath }).IsUnique();
        model.Entity<CatalogEntry>().HasIndex(x => x.ParentId);
        model.Entity<CatalogEntry>().HasOne<StorageRoot>().WithMany().HasForeignKey(x => x.RootId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FileOperation>().HasIndex(x => new { x.RootId, x.State });
        model.Entity<DocumentReview>().HasKey(x => x.EntryId);
        model.Entity<DocumentReview>().HasOne<CatalogEntry>().WithOne().HasForeignKey<DocumentReview>(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DocumentReview>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<FilingRuleDefinition>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<FilingRuleDefinition>().HasOne<CatalogEntry>().WithMany().HasForeignKey(x => x.DestinationFolderId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FilingRuleRevision>().HasIndex(x => new { x.RuleId, x.Revision }).IsUnique();
        model.Entity<FilingRuleRevision>().HasOne<FilingRuleDefinition>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FilingPreview>().HasIndex(x => x.EntryId);
        model.Entity<FileOperation>().HasIndex(x => x.FilingPreviewId).IsUnique();
    }
}
