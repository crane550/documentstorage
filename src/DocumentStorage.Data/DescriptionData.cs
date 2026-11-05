using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Data;

public sealed class DocumentDescription
{
    public Guid EntryId { get; set; }
    public string ContentHash { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public string[] Organizations { get; set; } = [];
    public string FactsJson { get; set; } = "{}";
    public string EvidenceJson { get; set; } = "{}";
    public string SearchText { get; set; } = "";
    public int Revision { get; set; }
    public Guid? SourceJobId { get; set; }
    public bool EditedByUser { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed partial class CatalogDb
{
    public DbSet<DocumentDescription> DocumentDescriptions => Set<DocumentDescription>();
    private static void ConfigureDescriptions(ModelBuilder model)
    {
        model.Entity<DocumentDescription>().HasKey(x=>x.EntryId);
        model.Entity<DocumentDescription>().Property(x=>x.Revision).IsConcurrencyToken();
        model.Entity<DocumentDescription>().HasOne<CatalogEntry>().WithOne().HasForeignKey<DocumentDescription>(x=>x.EntryId).OnDelete(DeleteBehavior.Restrict);
    }
}
