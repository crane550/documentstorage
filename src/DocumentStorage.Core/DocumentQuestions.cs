using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Core;

public sealed partial class LibraryService
{
    public static List<DocumentPassage> SplitPassages(CatalogEntry entry)
    {
        var result=new List<DocumentPassage>();var page=Path.GetExtension(entry.Name).Equals(".pdf",StringComparison.OrdinalIgnoreCase)?1:(int?)null;
        var offset=0;var ordinal=0;
        foreach(var section in entry.ExtractedText.Split('\f'))
        {
            for(var start=0;start<section.Length;)
            {
                if(char.IsLowSurrogate(section[start]))start++;
                if(start>=section.Length)break;
                var length=Math.Min(1400,section.Length-start);
                if(start+length<section.Length&&char.IsHighSurrogate(section[start+length-1]))length--;
                var text=section.Substring(start,length);
                if(!string.IsNullOrWhiteSpace(text))result.Add(new(){EntryId=entry.Id,Ordinal=ordinal++,Start=offset+start,Page=page,Title=entry.Name,Text=text,SourceRevision=DocumentPreview.Revision(entry)});
                if(start+length>=section.Length)break;start+=length-200;
            }
            offset+=section.Length+1;if(page!=null)page++;
        }
        return result;
    }
    public async Task<bool> IndexNextPassagesAsync(CancellationToken token=default)
    {
        await writer.WaitAsync(token);
        try
        {
            await using var db=await factory.CreateDbContextAsync(token);
            var entries=await db.Entries.Where(x=>!x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null&&x.TextStatus=="Indexed"&&x.PassageRevision=="").OrderBy(x=>x.Id).Take(20).ToListAsync(token);
            if(entries.Count==0)return false;
            await using var transaction=await db.Database.BeginTransactionAsync(token);
            foreach(var entry in entries)
            {
                await db.DocumentPassages.Where(x=>x.EntryId==entry.Id).ExecuteDeleteAsync(token);
                db.DocumentPassages.AddRange(SplitPassages(entry));entry.PassageRevision=Digest(entry.ExtractedText);
            }
            await db.SaveChangesAsync(token);await transaction.CommitAsync(token);return true;
        }
        finally {writer.Release();}
    }
    public async Task<(int Ready,int Pending)> PassageStatusAsync()
    {
        await using var db=await factory.CreateDbContextAsync();var eligible=db.Entries.Where(x=>!x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null&&x.TextStatus=="Indexed");
        return(await eligible.CountAsync(x=>x.PassageRevision!=""),await eligible.CountAsync(x=>x.PassageRevision==""));
    }
    private static async Task<IQueryable<CatalogEntry>> DeskScopeAsync(CatalogDb db,DeskConversation conversation)
    {
        var entries=db.Entries.Where(x=>!x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null&&x.TextStatus=="Indexed"&&x.PassageRevision!="");
        if(conversation.ScopeEntryId is Guid id)
        {
            var scope=await db.Entries.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id);
            if(scope==null||scope.IsMissing||scope.TrashBatchId!=null)throw new StorageException("This conversation's scope is unavailable. Start a conversation with another folder.");
            if(scope.IsFolder){var prefix=scope.RelativePath.Length==0?"":scope.RelativePath+"/";entries=entries.Where(x=>x.RootId==scope.RootId&&x.RelativePath.StartsWith(prefix));}
            else entries=entries.Where(x=>x.Id==id);
        }
        return entries;
    }
    private static readonly HashSet<string> QuestionStopWords=new("what which where when how much many does did do is are was were will would could should can please tell show about from with that this these those their them they have has had the and for not but into your our you its it any all give find know want need".Split(' '));
    private async Task<(string Json,string Hash)> QuestionContextAsync(CatalogDb db,DeskConversation conversation,string question,string history)
    {
        var entries=await DeskScopeAsync(db,conversation);
        var words=Regex.Matches((question+" "+history).ToLowerInvariant(),@"[\p{L}\p{N}]{2,}").Select(x=>x.Value).Where(x=>!QuestionStopWords.Contains(x)).Distinct().Take(32).ToArray();
        var joined=from passage in db.DocumentPassages join entry in entries on passage.EntryId equals entry.Id select new{Passage=passage,Entry=new CatalogEntry{Id=entry.Id,RootId=entry.RootId,Name=entry.Name,RelativePath=entry.RelativePath,Size=entry.Size,ModifiedAt=entry.ModifiedAt,PassageRevision=entry.PassageRevision}};
        var candidates=words.Length==0?[]:await joined.Where(x=>x.Passage.Search.Matches(EF.Functions.ToTsQuery("english",string.Join(" | ",words))))
            .OrderByDescending(x=>x.Passage.Search.Rank(EF.Functions.ToTsQuery("english",string.Join(" | ",words)))).ThenByDescending(x=>x.Entry.ModifiedAt).ThenBy(x=>x.Passage.Id).Take(100).ToListAsync();
        if(candidates.Count==0&&conversation.ScopeEntryId is Guid scope&&await entries.AnyAsync(x=>x.Id==scope))
        {candidates=await joined.OrderBy(x=>x.Passage.Ordinal).Take(8).ToListAsync();candidates.AddRange(await joined.OrderByDescending(x=>x.Passage.Ordinal).Take(4).ToListAsync());}
        var roots=await db.Roots.AsNoTracking().ToDictionaryAsync(x=>x.Id);
        var sources=new List<DeskSource>();var grouped=new Dictionary<Guid,int>();var budget=0;
        foreach(var row in candidates)
        {
            var p=row.Passage;var e=row.Entry;
            if(p.SourceRevision!=DocumentPreview.Revision(e)||grouped.GetValueOrDefault(e.Id)>=(conversation.ScopeEntryId==e.Id?12:4)||budget+p.Text.Length>18_000)continue;
            sources.Add(new("S"+(sources.Count+1),e.Id,e.Name,roots[e.RootId].Name+" / "+e.RelativePath,e.PassageRevision,p.Page,p.Start,p.Text,!roots[e.RootId].IsOnline));
            grouped[e.Id]=grouped.GetValueOrDefault(e.Id)+1;budget+=p.Text.Length;if(sources.Count==16)break;
        }
        var pending=await db.Entries.CountAsync(x=>!x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null&&x.TextStatus=="Indexed"&&x.PassageRevision=="");
        var json=JsonSerializer.Serialize(new{sources,indexing_pending=pending,search="Ranked lexical passages from indexed text; not an exhaustive library audit."});
        return(json,Digest(json));
    }
    private static List<DeskSource> Sources(DeskTurn turn)=>JsonSerializer.Deserialize<List<DeskSource>>(JsonDocument.Parse(turn.ContextJson).RootElement.GetProperty("sources").GetRawText())??[];
    private static async Task<bool> SourceCurrentAsync(CatalogDb db,DeskSource source)
    {
        var entry=await db.Entries.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==source.EntryId);
        return entry!=null&&!entry.IsFolder&&!entry.IsMissing&&entry.TrashBatchId==null&&entry.TextStatus=="Indexed"&&entry.PassageRevision!=""&&entry.PassageRevision==source.Revision&&
            source.Start+source.Text.Length<=entry.ExtractedText.Length&&entry.ExtractedText.Substring(source.Start,source.Text.Length)==source.Text;
    }
    public async Task<CatalogEntry> OpenDeskSourceAsync(Guid turnId,string sourceId,string actor)
    {
        await using var db=await factory.CreateDbContextAsync();var turn=await OwnedTurnAsync(db,turnId,actor);
        var source=Sources(turn).SingleOrDefault(x=>x.Id==sourceId)??throw new StorageException("Source not found.");
        if(!await SourceCurrentAsync(db,source))throw new StorageException("This source changed or left the library after the answer. Its saved excerpt remains available; ask again for a current answer.");
        return (await db.Entries.AsNoTracking().SingleAsync(x=>x.Id==source.EntryId));
    }
}
