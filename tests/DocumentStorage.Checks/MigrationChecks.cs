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
        db.Roots.Add(root); db.Entries.Add(folder); db.OllamaConfigurations.Add(settings); db.DocumentTypes.Add(type); await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        var versions = await db.InstructionRevisions.ToListAsync();
        check(versions.Count == 3 && versions.All(x=>x.Revision==1 && x.Actor=="imported-existing-guidance") &&
            versions.Single(x=>x.TargetKind=="folder").Text==folder.Instructions && versions.Single(x=>x.TargetKind=="global").Text==settings.Instructions && versions.Single(x=>x.TargetKind=="document_type").Text==type.Instructions,
            "upgrading an existing catalog preserves folder, general and document-type guidance as immutable baseline versions");
        db.InstructionRevisions.RemoveRange(versions); db.Entries.Remove(folder); db.Roots.Remove(root); db.OllamaConfigurations.Remove(settings); db.DocumentTypes.Remove(type); await db.SaveChangesAsync();
    }
}
