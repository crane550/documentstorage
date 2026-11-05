using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Core;

public sealed partial class LibraryService
{
    public Task<Guid> QueueOrganizationAsync(Guid entryId,string actor,bool retry=true) => QueueIdentificationAsync(entryId,actor,retry,organizing:true);
    private Task<IdentificationContext> JobContextAsync(CatalogDb db,CatalogEntry entry,OllamaSettings settings,IdentificationJob job)
        => job.Sorting?SortingContextAsync(db,entry,settings):job.Organizing?OrganizationContextAsync(db,entry,settings):IdentificationContextAsync(db,entry,settings);
    private static IdentificationResult ValidateOrganizationResult(IdentificationRequest input,IdentificationResult result)
    {
        var plan=result.Organization??throw new OllamaException("Missing filing proposal.");
        var value = new Dictionary<string,object> {
            ["document_type"]=result.DocumentType,["needs_review"]=result.NeedsReview,["summary"]=result.Summary[..Math.Min(600,result.Summary.Length)],
            ["facts"]=result.Facts.Select(x=>new {name=x.Key,value=x.Value,evidence=result.Evidence.GetValueOrDefault(x.Key)??""}),
            ["filing"]=new {destination_folder_id=plan.DestinationFolderId==Guid.Empty?"":plan.DestinationFolderId.ToString(),new_folder=plan.NewFolder,filename=plan.Filename,reason=plan.Reason}
        };
        if(result.Description is {} description)value["description"]=new {title=description.Title,tags=description.Tags,organizations=description.Organizations};
        return OllamaClient.Parse(JsonSerializer.Serialize(value),input);
    }
    private static HashSet<string> Words(string text) => Regex.Matches(text.ToLowerInvariant(),@"[\p{L}\p{N}]{3,}").Select(x=>x.Value).Take(3000).ToHashSet();
    private async Task<IdentificationContext> OrganizationContextAsync(CatalogDb db,CatalogEntry entry,OllamaSettings settings)
    {
        var all=await db.Entries.AsNoTracking().Where(x=>x.RootId==entry.RootId&&x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null).OrderBy(x=>x.Id)
            .Select(x=>new CatalogEntry {Id=x.Id,RootId=x.RootId,ParentId=x.ParentId,Name=x.Name,RelativePath=x.RelativePath,Instructions=x.Instructions,IsFolder=true,IsIntake=x.IsIntake}).ToListAsync();
        var byId=all.ToDictionary(x=>x.Id);var eligible=all.Where(x=>!HasIntakeAncestor(x,byId)).ToList();
        string Inherited(CatalogEntry folder)
        {
            var parts=new List<string>();CatalogEntry? cursor=folder;
            // Keep the most specific folder's notes visible when inherited guidance exceeds the prompt budget.
            while(cursor!=null) {if(cursor.Instructions.Length>0)parts.Add(cursor.Instructions);cursor=byId.GetValueOrDefault(cursor.ParentId??Guid.Empty);}
            return string.Join("\n\n",parts);
        }
        var destinationGuidance=eligible.ToDictionary(x=>x.Id,Inherited);
        var root=await db.Roots.AsNoTracking().SingleAsync(x=>x.Id==entry.RootId);
        var inherited=new List<string>();var current=byId.GetValueOrDefault(entry.ParentId??Guid.Empty);
        while(current!=null) { if(current.Instructions.Length>0)inherited.Insert(0,current.Instructions);current=byId.GetValueOrDefault(current.ParentId??Guid.Empty); }
        var savedDescription=await db.DocumentDescriptions.AsNoTracking().SingleOrDefaultAsync(x=>x.EntryId==entry.Id && x.ContentHash==entry.ContentHash);
        var words=Words((savedDescription?.SearchText??"")+" "+entry.Name+" "+entry.ExtractedText[..Math.Min(6000,entry.ExtractedText.Length)]+" "+string.Join(" ",inherited));
        var ranked=eligible.OrderByDescending(x=>Words(x.RelativePath+" "+destinationGuidance[x.Id]).Count(words.Contains)).ThenBy(x=>x.RelativePath,StringComparer.Ordinal).ToList();
        var candidates=new List<object>();var budget=0;
        foreach(var folder in ranked)
        {
            var guidanceText=destinationGuidance[folder.Id];
            var row=new {id=folder.Id,path=folder.RelativePath.Length==0?root.Name:root.Name+" / "+folder.RelativePath,guidance=guidanceText[..Math.Min(500,guidanceText.Length)]};
            var size=JsonSerializer.Serialize(row).Length;
            if(candidates.Count>=120||budget+size>16_000)continue;
            candidates.Add(row);budget+=size;
        }
        // Approvals add examples without invalidating other already queued jobs or plans.
        var examples=await db.FilingExamples.AsNoTracking().Where(x=>x.RootId==entry.RootId&&x.RemovedAt==null).OrderByDescending(x=>x.CreatedAt).Take(100).ToListAsync();
        var selected=examples.Where(x=>byId.TryGetValue(x.FolderId,out var f)&&!HasIntakeAncestor(f,byId))
            .OrderByDescending(x=>Words(x.OriginalName+" "+x.DocumentType+" "+x.Summary).Count(words.Contains)).ThenByDescending(x=>x.CreatedAt).Take(5)
            .Select(x=>new { document_type=x.DocumentType,original_name=x.OriginalName,approved_name=x.Filename,destination_folder_id=x.FolderId,summary=x.Summary[..Math.Min(240,x.Summary.Length)],note=x.Note[..Math.Min(240,x.Note.Length)] }).ToList();
        var truncated=candidates.Count<eligible.Count||string.Join("\n\n",inherited).Length>2000||destinationGuidance.Values.Any(x=>x.Length>500);
        var guidance=string.Join("\n\n",inherited);if(guidance.Length>2000)guidance=guidance[..2000];
        var policy=new { schema_version=2,settings.Instructions,source_guidance=guidance,root_name=root.Name,folders=candidates,total_eligible_folders=eligible.Count,can_create_folder=candidates.Count==eligible.Count };
        var known_tags=await KnownTagsAsync(db);
        var json=JsonSerializer.Serialize(new { known_tags,policy.schema_version,guidance=policy.Instructions,policy.source_guidance,policy.root_name,policy.folders,policy.total_eligible_folders,policy.can_create_folder,approved_examples=selected });
        var revisions=await db.FilingRules.AsNoTracking().OrderBy(x=>x.Id).Select(x=>new{x.Id,x.Revision}).ToListAsync();
        var forgotten=await db.FilingExamples.AsNoTracking().Where(x=>x.RootId==entry.RootId&&x.RemovedAt!=null).OrderBy(x=>x.Id).Select(x=>x.Id).ToListAsync();
        var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {schema_version=2,root.Id,root.Name,settings.Revision,settings.Instructions,forgotten,folders=all.Select(x=>new{x.Id,x.ParentId,x.RelativePath,x.Instructions,x.IsIntake}),rules=revisions}))));
        return new(json,hash,[],[],truncated,Organizing:true);
    }
}
