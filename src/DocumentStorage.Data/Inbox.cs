using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Data;

public sealed class InboxPosition
{
    public string Actor { get; set; } = "";
    public Guid? EntryId { get; set; }
    public int Offset { get; set; }
}

public sealed partial class CatalogDb
{
    public DbSet<InboxPosition> InboxPositions => Set<InboxPosition>();
    private static void ConfigureInbox(ModelBuilder model) => model.Entity<InboxPosition>().HasKey(x => x.Actor);
}
