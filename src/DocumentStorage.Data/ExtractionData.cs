using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Data;

public sealed class ExtractionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EntryId { get; set; }
    public string SourceRevision { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ExtractorVersion { get; set; } = "";
    public string State { get; set; } = "Queued";
    public int Attempts { get; set; }
    public Guid? RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
public sealed partial class CatalogDb
{
    public DbSet<ExtractionJob> ExtractionJobs => Set<ExtractionJob>();
    private static void ConfigureExtraction(ModelBuilder model)
    {
        model.Entity<ExtractionJob>().HasOne<CatalogEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ExtractionJob>().HasIndex(x => new { x.State, x.NotBefore });
        model.Entity<ExtractionJob>().HasIndex(x => new { x.EntryId, x.CreatedAt });
    }
}
