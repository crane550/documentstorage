using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
namespace DocumentStorage.Data;

public sealed class DocumentPassage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EntryId { get; set; }
    public int Ordinal { get; set; }
    public int Start { get; set; }
    public int? Page { get; set; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string SourceRevision { get; set; } = "";
    public NpgsqlTsVector Search { get; set; } = null!;
}
public sealed class DeskConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Owner { get; set; } = "";
    public string Mode { get; set; } = "ask";
    public string Title { get; set; } = "";
    public Guid? ScopeEntryId { get; set; }
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class DeskTurn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string RequestKey { get; set; } = "";
    public string Question { get; set; } = "";
    public string Reply { get; set; } = "";
    public string State { get; set; } = "Queued";
    public int SettingsRevision { get; set; }
    public string ContextJson { get; set; } = "{}";
    public string ContextHash { get; set; } = "";
    public string ResultJson { get; set; } = "{}";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class FolderPlanApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TurnId { get; set; }
    public Guid ScopeId { get; set; }
    public string Actor { get; set; } = "";
    public string ExpectedHash { get; set; } = "";
    public string State { get; set; } = "Pending";
    public string? Error { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class FolderPlanStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApprovalId { get; set; }
    public int Ordinal { get; set; }
    public Guid RootId { get; set; }
    public Guid FolderId { get; set; }
    public Guid ParentId { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public bool Create { get; set; }
    public string State { get; set; } = "Pending";
}
public sealed partial class CatalogDb
{
    public DbSet<DocumentPassage> DocumentPassages => Set<DocumentPassage>();
    public DbSet<DeskConversation> DeskConversations => Set<DeskConversation>();
    public DbSet<DeskTurn> DeskTurns => Set<DeskTurn>();
    public DbSet<FolderPlanApproval> FolderPlanApprovals => Set<FolderPlanApproval>();
    public DbSet<FolderPlanStep> FolderPlanSteps => Set<FolderPlanStep>();
    private static void ConfigureDesk(ModelBuilder model)
    {
        model.Entity<DocumentPassage>().HasOne<CatalogEntry>().WithMany().HasForeignKey(x=>x.EntryId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DocumentPassage>().HasIndex(x=>new {x.EntryId,x.Ordinal}).IsUnique();
        model.Entity<DocumentPassage>().HasGeneratedTsVectorColumn(x=>x.Search,"english",x=>new{x.Title,x.Text});
        model.Entity<DocumentPassage>().HasIndex(x=>x.Search).HasMethod("GIN");
        model.Entity<DeskConversation>().HasIndex(x=>new{x.Owner,x.UpdatedAt});
        model.Entity<DeskTurn>().HasOne<DeskConversation>().WithMany().HasForeignKey(x=>x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DeskTurn>().HasIndex(x=>new{x.ConversationId,x.RequestKey}).IsUnique();
        model.Entity<DeskTurn>().HasIndex(x=>new{x.State,x.CreatedAt});
        model.Entity<FolderPlanApproval>().HasOne<DeskTurn>().WithMany().HasForeignKey(x=>x.TurnId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FolderPlanStep>().HasOne<FolderPlanApproval>().WithMany().HasForeignKey(x=>x.ApprovalId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FolderPlanStep>().HasIndex(x=>new{x.ApprovalId,x.Ordinal}).IsUnique();
    }
}
