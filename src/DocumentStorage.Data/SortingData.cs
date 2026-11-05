using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Data;

// What a document is, who it's from and how it's tagged. Details live on the document
// (not the intake review), so they survive filing and follow app-managed moves.
public sealed class DocumentKind
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    // Patterns are relative to the storage location the inbox belongs to:
    // "Banking/{from}/{date:yyyy}" and "{date} {from} statement".
    public string FolderPattern { get; set; } = "";
    public string NamePattern { get; set; } = "";
    public string FieldsJson { get; set; } = "[]";
    public bool IsCatchAll { get; set; }
    // Files documents of this kind without asking, once the person has switched it on.
    public bool AutoFile { get; set; }
    public int Approved { get; set; }
    public int Corrected { get; set; }
    // Approvals in a row without a correction; any correction resets it.
    public int Streak { get; set; }
    public int Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Correspondent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string AliasesJson { get; set; } = "[]";
    public int Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string AliasesJson { get; set; } = "[]";
    public int Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DocumentDetail
{
    public Guid EntryId { get; set; }
    public Guid? KindId { get; set; }
    public Guid? CorrespondentId { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public string Title { get; set; } = "";
    public string FieldsJson { get; set; } = "{}";
    // Where each detail came from: filename, text, reader, person.
    public string SourcesJson { get; set; } = "{}";
    // Whether the folder and the name follow the kind's patterns. Only those parts move when a
    // pattern changes or senders merge; a kept or hand-picked folder or name stays put.
    public bool FollowsFolder { get; set; }
    public bool FollowsName { get; set; }
    public int Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed class EntryTag
{
    public Guid EntryId { get; set; }
    public Guid TagId { get; set; }
}

public sealed partial class CatalogDb
{
    public DbSet<DocumentKind> Kinds => Set<DocumentKind>();
    public DbSet<Correspondent> Correspondents => Set<Correspondent>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<DocumentDetail> Details => Set<DocumentDetail>();
    public DbSet<EntryTag> EntryTags => Set<EntryTag>();

    private static void ConfigureSorting(ModelBuilder model)
    {
        model.Entity<DocumentKind>().HasIndex(x => x.Name).IsUnique();
        model.Entity<DocumentKind>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<Correspondent>().HasIndex(x => x.Name).IsUnique();
        model.Entity<Correspondent>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<Tag>().HasIndex(x => x.Name).IsUnique();
        model.Entity<Tag>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<DocumentDetail>().HasKey(x => x.EntryId);
        model.Entity<DocumentDetail>().HasOne<CatalogEntry>().WithOne().HasForeignKey<DocumentDetail>(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DocumentDetail>().HasOne<DocumentKind>().WithMany().HasForeignKey(x => x.KindId).OnDelete(DeleteBehavior.SetNull);
        model.Entity<DocumentDetail>().HasOne<Correspondent>().WithMany().HasForeignKey(x => x.CorrespondentId).OnDelete(DeleteBehavior.SetNull);
        model.Entity<DocumentDetail>().HasIndex(x => x.KindId);
        model.Entity<DocumentDetail>().HasIndex(x => x.CorrespondentId);
        model.Entity<DocumentDetail>().HasIndex(x => x.DocumentDate);
        model.Entity<DocumentDetail>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<EntryTag>().HasKey(x => new { x.EntryId, x.TagId });
        model.Entity<EntryTag>().HasOne<CatalogEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<EntryTag>().HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<EntryTag>().HasIndex(x => x.TagId);
    }
}
