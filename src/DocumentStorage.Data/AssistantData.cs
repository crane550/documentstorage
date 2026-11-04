using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Data;

public sealed class InstructionMessage
{
    public string? RequestKey { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TargetKind { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public string Actor { get; set; } = "";
    public Guid? ConnectionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class InstructionRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TargetKind { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public int Revision { get; set; }
    public string Text { get; set; } = "";
    public string SourceMessagesJson { get; set; } = "[]";
    public string Actor { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class AssistantConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
public sealed class AssistantProposal
{
    public string SourceRequest { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public Guid? ConnectionId { get; set; }
    public string ConnectionKey { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string ContextHash { get; set; } = "";
    public string State { get; set; } = "Pending";
    public Guid? FilingPreviewId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string ApprovedBy { get; set; } = "";
    public string ResultJson { get; set; } = "{}";
    public int Revision { get; set; } = 1;
}
public sealed class AssistantAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ConnectionId { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public Guid? ProposalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed partial class CatalogDb
{
    public DbSet<InstructionMessage> InstructionMessages => Set<InstructionMessage>();
    public DbSet<InstructionRevision> InstructionRevisions => Set<InstructionRevision>();
    public DbSet<AssistantConnection> AssistantConnections => Set<AssistantConnection>();
    public DbSet<AssistantProposal> AssistantProposals => Set<AssistantProposal>();
    public DbSet<AssistantAudit> AssistantAudits => Set<AssistantAudit>();
    private static void ConfigureAssistants(ModelBuilder model)
    {
        model.UseOpenIddict();
        model.Entity<InstructionMessage>().HasIndex(x => new { x.Actor, x.ConnectionId, x.RequestKey }).IsUnique();
        model.Entity<InstructionMessage>().HasIndex(x => new { x.TargetKind, x.TargetKey, x.CreatedAt });
        model.Entity<InstructionRevision>().HasIndex(x => new { x.TargetKind, x.TargetKey, x.Revision }).IsUnique();
        model.Entity<AssistantConnection>().HasIndex(x => x.ClientId).IsUnique();
        model.Entity<AssistantConnection>().HasIndex(x => x.OwnerId);
        model.Entity<AssistantProposal>().HasIndex(x => new { x.OwnerId, x.ConnectionKey, x.IdempotencyKey }).IsUnique();
        model.Entity<AssistantProposal>().HasIndex(x => x.FilingPreviewId).IsUnique();
        model.Entity<AssistantProposal>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<AssistantAudit>().HasIndex(x => x.CreatedAt);
    }
}
