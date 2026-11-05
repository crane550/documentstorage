using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Core;

public sealed partial class LibraryService
{
    private static Task<string[]> KnownTagsAsync(CatalogDb db) => db.Database.SqlQuery<string>(
        $"""SELECT DISTINCT unnest("Tags") AS "Value" FROM "DocumentDescriptions" ORDER BY "Value" LIMIT 200""").ToArrayAsync();
    public async Task<string[]> KnownTagsAsync() { await using var db=await factory.CreateDbContextAsync();return await KnownTagsAsync(db); }
    public async Task<DocumentDescription?> DescriptionAsync(Guid id)
    {
        await using var db=await factory.CreateDbContextAsync();
        return await db.DocumentDescriptions.AsNoTracking().SingleOrDefaultAsync(x=>x.EntryId==id);
    }
    private static string[] DescriptionValues(IEnumerable<string> values,int max, bool tags=false)
    {
        var result=values.Select(x=>x.Trim()).Where(x=>x.Length>0).Select(x=>tags?x.ToLowerInvariant():x).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(result.Length>8||result.Any(x=>x.Length>max||x.Any(char.IsControl)))throw new StorageException("Use at most eight values and keep tags under 64 characters and organizations under 180.");
        return result;
    }
    private static void IndexDescription(DocumentDescription value) => value.SearchText=string.Join(" ",value.Title,value.Summary,string.Join(" ",value.Tags),string.Join(" ",value.Organizations),value.FactsJson);
    private async Task StoreDescriptionAsync(CatalogDb db,CatalogEntry entry,IdentificationJob job,IdentificationResult result)
    {
        var saved=await db.DocumentDescriptions.SingleOrDefaultAsync(x=>x.EntryId==entry.Id);
        // Placement never revises understanding; a full reread preserves user corrections for unchanged bytes.
        if(job.FromSavedDescription || saved is {EditedByUser:true} && saved.ContentHash==entry.ContentHash)return;
        if(saved==null){saved=new(){EntryId=entry.Id};db.DocumentDescriptions.Add(saved);}
        saved.ContentHash=entry.ContentHash;saved.Title=result.Description?.Title??Path.GetFileNameWithoutExtension(entry.Name);
        saved.Summary=result.Summary;saved.Tags=DescriptionValues(result.Description?.Tags??[result.DocumentType.Replace('_',' ')],64,true);
        saved.Organizations=DescriptionValues(result.Description?.Organizations??[],180);
        saved.FactsJson=JsonSerializer.Serialize(result.Facts);saved.EvidenceJson=JsonSerializer.Serialize(result.Evidence);
        saved.Revision++;saved.SourceJobId=job.Id;saved.EditedByUser=false;saved.UpdatedAt=time.GetUtcNow();saved.UpdatedBy=job.CreatedBy;IndexDescription(saved);
    }
    public async Task SaveDescriptionAsync(Guid id,int revision,string title,string summary,string[] tags,string[] organizations,string actor)
    {
        Actor(actor);title=title.Trim();summary=summary.Trim();
        if(title.Length is 0 or >180||summary.Length>1000||title.Any(char.IsControl)||summary.Contains('\0'))throw new StorageException("Enter a title under 180 characters and a summary under 1,000.");
        tags=DescriptionValues(tags,64,true);organizations=DescriptionValues(organizations,180);
        await writer.WaitAsync();try
        {
            await using var db=await factory.CreateDbContextAsync();var(entry,root)=await WritableEntryAsync(db,id);
            if(entry.IsFolder||entry.TrashBatchId!=null)throw new StorageException("Choose an available document.");
            var saved=await db.DocumentDescriptions.SingleOrDefaultAsync(x=>x.EntryId==id)??throw new StorageException("Read this document first to save its description.");
            if(saved.Revision!=revision||saved.ContentHash!=entry.ContentHash||await FingerprintAsync(root,entry)!=saved.ContentHash)throw new StorageException("The document or description changed. Refresh before saving.");
            saved.Title=title;saved.Summary=summary;saved.Tags=tags;saved.Organizations=organizations;saved.Revision++;saved.EditedByUser=true;saved.UpdatedBy=actor;saved.UpdatedAt=time.GetUtcNow();IndexDescription(saved);
            await db.SaveChangesAsync();
        }finally{writer.Release();}
    }
    private static string PlacementText(DocumentDescription saved)=>"SAVED DOCUMENT DESCRIPTION (not original OCR)\n"+saved.Title+"\n"+saved.Summary+"\nTags: "+string.Join(", ",saved.Tags)+"\nOrganizations: "+string.Join(", ",saved.Organizations)+"\nFacts: "+saved.FactsJson+"\nEvidence: "+string.Join("\n",JsonSerializer.Deserialize<Dictionary<string,string>>(saved.EvidenceJson)!.Values);
    public async Task<Guid> QueuePlacementAsync(Guid id,string actor)
    {
        Actor(actor);await writer.WaitAsync();try
        {
            await using var db=await factory.CreateDbContextAsync();var(entry,root)=await WritableEntryAsync(db,id);await RequireIntakeAsync(db,entry);RequireStable(entry);
            var saved=await db.DocumentDescriptions.SingleOrDefaultAsync(x=>x.EntryId==id);
            if(saved==null||saved.ContentHash!=entry.ContentHash||await FingerprintAsync(root,entry)!=saved.ContentHash)throw new StorageException("A current saved description is required. Read the changed document first.");
            var settings=await AiDefaultsAsync(db);if(!settings.Enabled)throw new StorageException("Enable the AI reader first.");
            var active=await db.IdentificationJobs.FirstOrDefaultAsync(x=>x.EntryId==id&&(x.State=="Queued"||x.State=="Running"||x.State=="Retry"));if(active!=null)return active.Id;
            var context=await OrganizationContextAsync(db,entry,settings);
            var job=new IdentificationJob {EntryId=id,ContentHash=entry.ContentHash,SourcePath=entry.RelativePath,SettingsRevision=settings.Revision,ContextHash=context.Hash,ContextJson=context.Json,Model=settings.Model,Endpoint=settings.Endpoint,Organizing=true,FromSavedDescription=true,DescriptionRevision=saved.Revision,
                ReviewRevision=(await db.Reviews.SingleOrDefaultAsync(x=>x.EntryId==id))?.Revision??0,CreatedAt=time.GetUtcNow(),NotBefore=time.GetUtcNow(),CreatedBy=actor};
            db.IdentificationJobs.Add(job);await db.SaveChangesAsync();return job.Id;
        }finally{writer.Release();}
    }
    private static async Task<bool> DescriptionMatchesAsync(CatalogDb db,IdentificationJob job)=>!job.FromSavedDescription||await db.DocumentDescriptions.AnyAsync(x=>x.EntryId==job.EntryId&&x.ContentHash==job.ContentHash&&x.Revision==job.DescriptionRevision);
}
