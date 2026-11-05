using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

internal static class MigrationChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, Action<bool,string> check)
    {
        await using var db = await factory.CreateDbContextAsync(); await db.Database.EnsureDeletedAsync();
        var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261104025430_AddLocalIdentification");
        var root = new StorageRoot { Name = "Synthetic legacy guidance", Location = Path.Combine(Path.GetTempPath(),"synthetic-legacy-guidance") };
        var folder = new CatalogEntry { RootId = root.Id, Name = "Legacy folder", IsFolder = true, Instructions = "Keep the original folder instructions." };
        var settings = new OllamaSettings { Instructions = "Keep the original global instructions." };
        var type = new DocumentTypeProfile { Key = "legacy_type", Name = "Legacy type", Instructions = "Keep the original type instructions." };
        db.Roots.Add(root); db.OllamaConfigurations.Add(settings); db.DocumentTypes.Add(type); await db.SaveChangesAsync();
        // Seed the old schema, before the additive passage-index column exists.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Entries" ("Id","RootId","RelativePath","Name","IsFolder","IsMissing","Size","ModifiedAt","Instructions","ExtractedText","TextStatus")
            VALUES ({folder.Id},{root.Id},{""},{folder.Name},{true},{false},{0L},{DateTimeOffset.UtcNow},{folder.Instructions},{""},{"Pending"})
            """);
        var legacyOperation = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Operations" ("Id", "RootId", "EntryId", "Kind", "FromPath", "ToPath", "State", "CreatedAt")
            VALUES ({legacyOperation}, {root.Id}, {folder.Id}, {"Move"}, {"legacy-source"}, {"legacy-target"}, {"Needs review"}, {DateTimeOffset.UtcNow})
            """);
        await migrator.MigrateAsync("20261105001255_AddInboxProgressAndReaderInput");
        var legacyDocument=new CatalogEntry { RootId=root.Id,Name="legacy-guide.txt",RelativePath="legacy-guide.txt",ContentHash="synthetic-legacy-fingerprint" };
        db.Entries.Add(legacyDocument);await db.SaveChangesAsync();var legacyJob=Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IdentificationJobs" ("Id","EntryId","ContentHash","SourcePath","ReviewRevision","SettingsRevision","ContextHash","ContextJson","Endpoint","Model","InputTruncated","State","Attempts","CreatedAt","CreatedBy","NotBefore","DocumentType","FactsJson","EvidenceJson","Summary","NeedsReview")
            VALUES ({legacyJob},{legacyDocument.Id},{legacyDocument.ContentHash},{legacyDocument.RelativePath},{0},{1},{"context"},{"{}"},{"http://127.0.0.1:11434/"},{"synthetic:latest"},{false},{"Succeeded"},{1},{DateTimeOffset.UtcNow},{"owner"},{DateTimeOffset.UtcNow},{"training_guide"},{"{}"},{"{}"},{"Synthetic saved guide summary."},{false})
            """);
        await migrator.MigrateAsync();
        var imported=await db.DocumentDescriptions.SingleAsync(x=>x.EntryId==legacyDocument.Id);
        check(imported.SourceJobId==legacyJob&&imported.Summary=="Synthetic saved guide summary."&&imported.Tags.Contains("training guide"),"description migration reuses an existing saved reading without inference");
        db.DocumentDescriptions.Remove(imported);db.IdentificationJobs.Remove(await db.IdentificationJobs.SingleAsync(x=>x.Id==legacyJob));db.Entries.Remove(legacyDocument);

        var retained = await db.Operations.SingleAsync(x => x.Id == legacyOperation);
        check(retained.State == "Needs review" && retained.SourceSnapshotJson == "" && !await db.OperationRecoveries.AnyAsync(),
            "recovery migration preserves an existing blocked journal without inventing fingerprints or decisions");
        db.Operations.Remove(retained);
        var versions = await db.InstructionRevisions.ToListAsync();
        check(versions.Count == 3 && versions.All(x=>x.Revision==1 && x.Actor=="imported-existing-guidance") &&
            versions.Single(x=>x.TargetKind=="folder").Text==folder.Instructions && versions.Single(x=>x.TargetKind=="global").Text==settings.Instructions && versions.Single(x=>x.TargetKind=="document_type").Text==type.Instructions,
            "upgrading an existing catalog preserves folder, general and document-type guidance as immutable baseline versions");
        db.InstructionRevisions.RemoveRange(versions); db.Entries.Remove(folder); db.Roots.Remove(root); db.OllamaConfigurations.Remove(settings); db.DocumentTypes.Remove(type); await db.SaveChangesAsync();
    }
}
