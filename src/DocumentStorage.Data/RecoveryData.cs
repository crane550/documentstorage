using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Data;

// An inspection is an actor-bound, expiring decision token. Applied rows are immutable receipts.
public sealed class OperationRecovery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string ObservationHash { get; set; } = "";
    public string ObservationJson { get; set; } = "";
    public string Decision { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTimeOffset? AppliedAt { get; set; }
}

public sealed partial class CatalogDb
{
    public DbSet<OperationRecovery> OperationRecoveries => Set<OperationRecovery>();
    private static void ConfigureRecovery(ModelBuilder model)
    {
        model.Entity<OperationRecovery>().HasOne<FileOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<OperationRecovery>().HasIndex(x => new { x.OperationId, x.CreatedAt });
        model.Entity<OperationRecovery>().HasIndex(x => x.ExpiresAt);
    }
}
