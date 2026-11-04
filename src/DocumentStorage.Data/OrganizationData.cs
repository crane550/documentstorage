using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Data;

// Exact, actor-approved filing plans. Examples are separate opt-in records, committed with the move.
public sealed class OrganizationFiling
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid EntryId { get; set; }
    public string Actor { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string ContextHash { get; set; } = "";
    public int ReviewRevision { get; set; }
    public int SettingsRevision { get; set; }
    public Guid ParentFolderId { get; set; }
    public string ParentPath { get; set; } = "";
    public string NewFolderName { get; set; } = "";
    public Guid TargetFolderId { get; set; }
    public string Filename { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string FactsJson { get; set; } = "{}";
    public string Reason { get; set; } = "";
    public bool Remember { get; set; }
    public string State { get; set; } = "Pending";
    public Guid? OperationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
public sealed class FilingExample
{
    public DateTimeOffset? RemovedAt { get; set; }
    public string RemovedBy { get; set; } = "";
    public Guid Id { get; set; }
    public Guid RootId { get; set; }
    public Guid EntryId { get; set; }
    public Guid FolderId { get; set; }
    public string OriginalName { get; set; } = "";
    public string Filename { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Note { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed partial class CatalogDb
{
    public DbSet<OrganizationFiling> OrganizationFilings => Set<OrganizationFiling>();
    public DbSet<FilingExample> FilingExamples => Set<FilingExample>();
    private static void ConfigureOrganization(ModelBuilder model)
    {
        model.Entity<OrganizationFiling>().HasOne<IdentificationJob>().WithMany().HasForeignKey(x=>x.JobId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<OrganizationFiling>().HasIndex(x=>new { x.EntryId,x.CreatedAt });
        model.Entity<OrganizationFiling>().HasIndex(x=>x.OperationId).IsUnique();
        model.Entity<FileOperation>().HasOne<OrganizationFiling>().WithMany().HasForeignKey(x=>x.OrganizationFilingId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FileOperation>().HasIndex(x=>x.OrganizationFilingId).IsUnique();
        model.Entity<FilingExample>().HasOne<OrganizationFiling>().WithOne().HasForeignKey<FilingExample>(x=>x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FilingExample>().HasIndex(x=>new { x.RootId,x.CreatedAt });
    }
}
