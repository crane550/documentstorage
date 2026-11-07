using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Core;

public sealed record AssistantDetailsEdit(DetailsProposal Details,int ExpectedRevision,string ContentHash);
public sealed record AssistantKindEdit(KindProposal Kind,int ExpectedRevision);
public sealed record AssistantNameEdit(Guid? Id,string Name,string[] Aliases,int ExpectedRevision);
public sealed record AssistantExampleEdit(Guid DocumentId,int ExpectedRevision,string ContentHash,string Lesson);
public sealed record AssistantDiagnosticRead(Guid DocumentId,string ContentHash);
public sealed record AssistantExampleRemoval(Guid ExampleId,int ExpectedRevision);
public sealed record AssistantEditResult(Guid ReceiptId,string Action,Guid TargetId,int Revision,bool Applied,string State="Applied");

public sealed partial class LibraryService
{
    private static object TeachingPromptExample(ReaderTeachingExample example,string kind)
    {
        using var value=JsonDocument.Parse(example.ChoiceJson);var root=value.RootElement;
        var fields=root.GetProperty("fields").EnumerateObject().Take(4).ToDictionary(x=>x.Name,x=>(x.Value.GetString()??"")[..Math.Min(100,(x.Value.GetString()??"").Length)]);
        return new {kind,from=root.GetProperty("from").GetString(),date=root.GetProperty("date").GetString(),title=root.GetProperty("title").GetString(),
            tags=root.GetProperty("tags").EnumerateArray().Take(4).Select(x=>x.GetString()).ToArray(),fields,text=example.TextExcerpt[..Math.Min(500,example.TextExcerpt.Length)],lesson=example.Lesson[..Math.Min(240,example.Lesson.Length)]};
    }
    // One writer/transaction binds authorization, optimistic revisions, the change, receipt and audit.
    // These operations never approve a proposal, move a file, or change unattended-filing switches.
    public async Task<AssistantEditResult> EditAssistantAsync(string action,string payload,string contextHash,string idempotencyKey,string sourceRequest,AssistantCaller caller,bool applyNow=false)
    {
        if(caller.ConnectionId==null)throw new StorageException("connection_required: An assistant connection is required.");
        if(payload.Length>40000||sourceRequest.Length is 0 or >4000||idempotencyKey.Length is <8 or >100||idempotencyKey.Any(char.IsControl))throw new StorageException("invalid_request: Supply bounded input, a source request and an idempotency key of 8–100 characters.");
        await writer.WaitAsync();try
        {
            await using var db=await factory.CreateDbContextAsync();await CheckCallerAsync(db,caller,"sorting.manage");await CheckCallerAsync(db,caller,"documents.read");
            var hash=Digest(action+"\n"+applyNow+"\n"+contextHash+"\n"+payload+"\n"+sourceRequest);
            var old=await db.AssistantEdits.AsNoTracking().SingleOrDefaultAsync(x=>x.Actor==caller.Actor&&x.ConnectionId==caller.ConnectionId&&x.IdempotencyKey==idempotencyKey);
            if(old!=null)
            {
                if(old.PayloadHash!=hash)throw new StorageException("idempotency_conflict: This key already describes a different edit.");
                var recorded=JsonSerializer.Deserialize<AssistantEditResult>(old.ResultJson)!;
                return recorded with {State=old.State,Applied=old.State is "Applied" or "Verified"};
            }
            if((await AssistantContextLockedAsync(db)).Hash!=contextHash)throw new StorageException("stale_context: Read get_context again before editing.");
            await using var transaction=await db.Database.BeginTransactionAsync();
            var connection=await db.AssistantConnections.SingleAsync(x=>x.Id==caller.ConnectionId);
            var pending=connection.ReviewChangesFirst&&!applyNow;
            if(pending)await ValidateAssistantEditAsync(db,action,payload);
            var outcome=pending?(Guid.Empty,0,"{}"):await ApplyAssistantEditLockedAsync(db,action,payload,caller);
            if(!pending)await db.SaveChangesAsync();
            var after=pending?"{}":await AssistantSnapshotAsync(db,action,outcome.Item1);
            var before=pending?await AssistantPendingSnapshotAsync(db,action,payload):outcome.Item3;
            var receipt=new AssistantEdit{AfterJson=after,State=pending?"Pending":"Applied",PayloadJson=payload,ContextHash=contextHash,BeforeJson=before,Actor=caller.Actor,ConnectionId=caller.ConnectionId.Value,Action=action,PayloadHash=hash,IdempotencyKey=idempotencyKey,SourceRequest=sourceRequest,CreatedAt=time.GetUtcNow()};
            var result=new AssistantEditResult(receipt.Id,action,outcome.Item1,outcome.Item2,!pending,receipt.State);receipt.ResultJson=JsonSerializer.Serialize(result);db.AssistantEdits.Add(receipt);
            Audit(db,caller,"sorting.edit."+action,receipt.State,receipt.Id);await db.SaveChangesAsync();await transaction.CommitAsync();return result;
        }finally{writer.Release();}
    }
    private async Task<string> AssistantPendingSnapshotAsync(CatalogDb db,string action,string payload)
    {
        var id=action switch {"details"=>JsonSerializer.Deserialize<AssistantDetailsEdit>(payload)!.Details.DocumentId,"kind"=>JsonSerializer.Deserialize<AssistantKindEdit>(payload)!.Kind.KindId??Guid.Empty,"sender" or "tag"=>JsonSerializer.Deserialize<AssistantNameEdit>(payload)!.Id??Guid.Empty,"forget_example"=>JsonSerializer.Deserialize<AssistantExampleRemoval>(payload)!.ExampleId,_=>Guid.Empty};
        return id==Guid.Empty?"{}":await AssistantSnapshotAsync(db,action,id);
    }
    private static async Task<string> AssistantSnapshotAsync(CatalogDb db,string action,Guid id)
    {
        object? value=null;
        switch(action)
        {
            case "details":
                var detail=await db.Details.AsNoTracking().SingleOrDefaultAsync(x=>x.EntryId==id);
                if(detail!=null)value=new {kind=detail.KindId==null?"":(await db.Kinds.SingleAsync(x=>x.Id==detail.KindId)).Name,from=detail.CorrespondentId==null?"":(await db.Correspondents.SingleAsync(x=>x.Id==detail.CorrespondentId)).Name,detail.DocumentDate,detail.Title,tags=await db.EntryTags.Where(x=>x.EntryId==id).Join(db.Tags,x=>x.TagId,x=>x.Id,(x,t)=>t.Name).ToArrayAsync(),fields=JsonSerializer.Deserialize<JsonElement>(detail.FieldsJson)};
                break;
            case "kind":var kind=await db.Kinds.FindAsync(id);if(kind!=null)value=new {kind.Name,kind.Description,kind.FolderPattern,kind.NamePattern,fields=KindFields(kind),field_guides=FieldGuides(kind)};break;
            case "sender":var sender=await db.Correspondents.FindAsync(id);if(sender!=null)value=new {sender.Name,aliases=Aliases(sender.AliasesJson)};break;
            case "tag":var tag=await db.Tags.FindAsync(id);if(tag!=null)value=new {tag.Name,aliases=Aliases(tag.AliasesJson)};break;
            case "reread":var job=await db.IdentificationJobs.FindAsync(id);if(job!=null)value=new {document_id=job.EntryId,job.Model,job.State,diagnostic=true};break;
            case "example":case "forget_example":var example=await db.ReaderTeachingExamples.FindAsync(id);if(example!=null)value=new {example.Lesson,details=JsonSerializer.Deserialize<JsonElement>(example.ChoiceJson),example.RemovedAt};break;
        }
        return value==null?"{}":JsonSerializer.Serialize(value);
    }
    private async Task<(CatalogEntry,OllamaSettings)> ValidateDiagnosticReadAsync(CatalogDb db,AssistantDiagnosticRead request)
    {
        var(entry,root)=await WritableEntryAsync(db,request.DocumentId);await RequireIntakeAsync(db,entry);RequireStable(entry);
        var settings=await AiDefaultsAsync(db);
        if(!settings.Enabled)throw new StorageException("reader_disabled: Enable the local reader first.");
        if(entry.ContentHash!=request.ContentHash||await FingerprintAsync(root,entry)!=request.ContentHash)throw new StorageException("stale_document: Inspect the current document before requesting a diagnostic reading.");
        if(entry.TextStatus!="Indexed"||string.IsNullOrWhiteSpace(entry.ExtractedText))throw new StorageException("text_unavailable: Restore indexed text first.");
        if(await db.IdentificationJobs.AnyAsync(x=>x.EntryId==entry.Id&&(x.State=="Queued"||x.State=="Running"||x.State=="Retry")))throw new StorageException("reader_busy: A reading is already active. Inspect its saved progress first.");
        return(entry,settings);
    }
    private async Task ValidateAssistantEditAsync(CatalogDb db,string action,string payload)
    {
        switch(action)
        {
            case "details":
                var d=JsonSerializer.Deserialize<AssistantDetailsEdit>(payload)!;await ValidateSortingProposalAsync(db,"Details",JsonSerializer.Serialize(d.Details));
                var (entry,root)=await WritableEntryAsync(db,d.Details.DocumentId);
                if((await db.Details.Where(x=>x.EntryId==entry.Id).Select(x=>(int?)x.Revision).SingleOrDefaultAsync()??0)!=d.ExpectedRevision||entry.ContentHash!=d.ContentHash||await FingerprintAsync(root,entry)!=d.ContentHash)throw new StorageException("stale_document: Inspect the current document again.");break;
            case "kind":
                var k=JsonSerializer.Deserialize<AssistantKindEdit>(payload)!;await ValidateSortingProposalAsync(db,"Kind",JsonSerializer.Serialize(k.Kind));
                if(k.ExpectedRevision!=(k.Kind.KindId==null?0:await db.Kinds.Where(x=>x.Id==k.Kind.KindId).Select(x=>x.Revision).SingleAsync()))throw new StorageException("stale_kind: Read the kind again.");break;
            case "sender":case "tag":
                var n=JsonSerializer.Deserialize<AssistantNameEdit>(payload)!;if(action=="sender")SenderName(n.Name);else TagName(n.Name);
                if(n.Aliases.Length>30||n.Aliases.Any(x=>x==null||x.Length>80))throw new StorageException("invalid_aliases: Use at most 30 short aliases.");
                var revision=n.Id==null?0:action=="sender"?await db.Correspondents.Where(x=>x.Id==n.Id).Select(x=>(int?)x.Revision).SingleOrDefaultAsync():await db.Tags.Where(x=>x.Id==n.Id).Select(x=>(int?)x.Revision).SingleOrDefaultAsync();
                if(revision!=n.ExpectedRevision)throw new StorageException("stale_target: Read the current sender/tag revision.");break;
            case "example":
                var e=JsonSerializer.Deserialize<AssistantExampleEdit>(payload)!;
                if(e.Lesson.Length is 0 or >600)throw new StorageException("invalid_lesson: Use a lesson of 1–600 characters.");
                if(!await db.Details.AnyAsync(x=>x.EntryId==e.DocumentId&&x.KindId!=null&&x.Revision==e.ExpectedRevision)||!await db.Entries.AnyAsync(x=>x.Id==e.DocumentId&&!x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null&&x.ContentHash==e.ContentHash))throw new StorageException("stale_document: Save current corrected details first.");break;
            case "reread":
                var r=JsonSerializer.Deserialize<AssistantDiagnosticRead>(payload)!;await ValidateDiagnosticReadAsync(db,r);break;
            case "forget_example":
                var f=JsonSerializer.Deserialize<AssistantExampleRemoval>(payload)!;if(!await db.ReaderTeachingExamples.AnyAsync(x=>x.Id==f.ExampleId&&x.Revision==f.ExpectedRevision))throw new StorageException("stale_example: Read the example again.");break;
            default:throw new StorageException("unsupported_edit: Unknown edit action.");
        }
    }
    private async Task<(Guid,int,string)> ApplyAssistantEditLockedAsync(CatalogDb db,string action,string payload,AssistantCaller caller)
    {
            Guid target;int revision;string before="{}";
            switch(action)
            {
                case "details":
                {
                    var input=JsonSerializer.Deserialize<AssistantDetailsEdit>(payload)!;
                    await ValidateSortingProposalAsync(db,"Details",JsonSerializer.Serialize(input.Details));
                    var (entry,root)=await WritableEntryAsync(db,input.Details.DocumentId);
                    var current=await db.Details.SingleOrDefaultAsync(x=>x.EntryId==entry.Id);
                    if((current?.Revision??0)!=input.ExpectedRevision||entry.ContentHash!=input.ContentHash||await FingerprintAsync(root,entry)!=input.ContentHash)throw new StorageException("stale_document: Reread the current document fingerprint and details revision.");
                    if(current!=null)before=JsonSerializer.Serialize(new {kind=current.KindId==null?"":(await db.Kinds.SingleAsync(x=>x.Id==current.KindId)).Name,from=current.CorrespondentId==null?"":(await db.Correspondents.SingleAsync(x=>x.Id==current.CorrespondentId)).Name,date=current.DocumentDate,title=current.Title,fields=JsonSerializer.Deserialize<JsonElement>(current.FieldsJson),tags=await db.EntryTags.Where(x=>x.EntryId==entry.Id).Join(db.Tags,x=>x.TagId,x=>x.Id,(x,t)=>t.Name).ToArrayAsync()});
                    var value=input.Details;
                    await SaveDetailsCoreAsync(db,entry.Id,new(value.Kind,value.From,ProposalDate(value.Date),value.Title,value.Tags,value.Fields),caller.Actor,"");
                    current=await db.Details.FindAsync(entry.Id);
                    current!.SourcesJson=JsonSerializer.Serialize(new[]{"kind","from","date","title","tags"}.Concat(value.Fields.Keys).Distinct().ToDictionary(x=>x,_=>"assistant"));
                    current.UpdatedBy="assistant:"+caller.ConnectionId;target=entry.Id;revision=current.Revision;break;
                }
                case "kind":
                {
                    var input=JsonSerializer.Deserialize<AssistantKindEdit>(payload)!;var value=input.Kind;
                    await ValidateSortingProposalAsync(db,"Kind",JsonSerializer.Serialize(value));
                    var current=value.KindId==null?null:await db.Kinds.SingleAsync(x=>x.Id==value.KindId);
                    if((current?.Revision??0)!=input.ExpectedRevision)throw new StorageException("stale_kind: Read the kind again before editing.");
                    if(current!=null)before=JsonSerializer.Serialize(new {current.Name,current.Description,current.FolderPattern,current.NamePattern,fields=KindFields(current),guides=FieldGuides(current)});
                    if(current==null){current=new(){Revision=0,CreatedAt=time.GetUtcNow()};db.Kinds.Add(current);}
                    current.Name=KindName(value.Name);current.Description=value.Description.Trim();current.FieldsJson=JsonSerializer.Serialize(value.Fields.Select(FactKey).Distinct().ToArray());
                    current.FolderPattern=SortPatterns.ValidatePattern(value.FolderPattern,true);current.NamePattern=SortPatterns.ValidatePattern(value.NamePattern,false);
                    if(value.Guides!=null)current.FieldGuideJson=JsonSerializer.Serialize(value.Guides.ToDictionary(x=>x.Key,x=>CleanGuide(x.Key,x.Value)));
                    current.Revision++;target=current.Id;revision=current.Revision;break;
                }
                case "sender":
                case "tag":
                {
                    var input=JsonSerializer.Deserialize<AssistantNameEdit>(payload)!;
                    if(input.Aliases.Length>30||input.Aliases.Any(x=>x.Length>80))throw new StorageException("invalid_aliases: Use at most 30 short aliases.");
                    if(action=="sender")
                    {
                        var current=input.Id==null?null:await db.Correspondents.SingleOrDefaultAsync(x=>x.Id==input.Id);
                        if(input.Id!=null&&current==null)throw new StorageException("not_found: Sender not found.");
                        if((current?.Revision??0)!=input.ExpectedRevision)throw new StorageException("stale_sender: Read the sender again.");
                        var name=SenderName(input.Name);if(await db.Correspondents.AnyAsync(x=>x.Id!=input.Id&&x.Name.ToLower()==name.ToLower()))throw new StorageException("name_exists: Merge existing senders instead.");
                        if(current==null){current=new(){Revision=0,CreatedAt=time.GetUtcNow()};db.Correspondents.Add(current);}
                        if(current.Revision>0)before=JsonSerializer.Serialize(new {current.Name,aliases=Aliases(current.AliasesJson)});
                        current.Name=name;current.AliasesJson=JsonSerializer.Serialize(CleanAliases(input.Aliases,name));current.Revision++;target=current.Id;revision=current.Revision;
                    }
                    else
                    {
                        var current=input.Id==null?null:await db.Tags.SingleOrDefaultAsync(x=>x.Id==input.Id);
                        if(input.Id!=null&&current==null)throw new StorageException("not_found: Tag not found.");
                        if((current?.Revision??0)!=input.ExpectedRevision)throw new StorageException("stale_tag: Read the tag again.");
                        var name=TagName(input.Name);if(await db.Tags.AnyAsync(x=>x.Id!=input.Id&&x.Name==name))throw new StorageException("name_exists: Merge existing tags instead.");
                        if(current==null){current=new(){Revision=0,CreatedAt=time.GetUtcNow()};db.Tags.Add(current);}
                        if(current.Revision>0)before=JsonSerializer.Serialize(new {current.Name,aliases=Aliases(current.AliasesJson)});
                        current.Name=name;current.AliasesJson=JsonSerializer.Serialize(input.Aliases.Select(TagName).Where(x=>x!=name).Distinct().ToArray());current.Revision++;target=current.Id;revision=current.Revision;
                    }
                    break;
                }
                case "example":
                {
                    var input=JsonSerializer.Deserialize<AssistantExampleEdit>(payload)!;
                    if(input.Lesson.Length is 0 or >600)throw new StorageException("invalid_lesson: Provide a lesson of 1–600 characters.");
                    var(entry,root)=await WritableEntryAsync(db,input.DocumentId);var detail=await db.Details.AsNoTracking().SingleOrDefaultAsync(x=>x.EntryId==entry.Id);
                    if(entry.IsFolder||entry.TrashBatchId!=null||detail?.KindId==null)throw new StorageException("details_required: Save the corrected document details first.");
                    if(detail.Revision!=input.ExpectedRevision||entry.ContentHash!=input.ContentHash||await FingerprintAsync(root,entry)!=input.ContentHash)throw new StorageException("stale_document: The document or details changed.");
                    var sender=detail.CorrespondentId==null?"":(await db.Correspondents.SingleAsync(x=>x.Id==detail.CorrespondentId)).Name;
                    var tags=await db.EntryTags.Where(x=>x.EntryId==entry.Id).Join(db.Tags,x=>x.TagId,x=>x.Id,(x,t)=>t.Name).ToArrayAsync();
                    var kind=await db.Kinds.SingleAsync(x=>x.Id==detail.KindId);
                    var example=new ReaderTeachingExample{EntryId=entry.Id,RootId=entry.RootId,KindId=kind.Id,ContentHash=entry.ContentHash,DetailsRevision=detail.Revision,
                        ChoiceJson=JsonSerializer.Serialize(new {kind=kind.Name,from=sender,date=detail.DocumentDate?.ToString("yyyy-MM-dd"),title=detail.Title,tags,fields=JsonSerializer.Deserialize<Dictionary<string,string>>(detail.FieldsJson)}),
                        TextExcerpt=entry.ExtractedText[..Math.Min(1200,entry.ExtractedText.Length)],Lesson=input.Lesson,Actor=caller.Actor,ConnectionId=caller.ConnectionId??throw new StorageException("Connection required."),CreatedAt=time.GetUtcNow()};
                    db.ReaderTeachingExamples.Add(example);target=example.Id;revision=example.Revision;break;
                }
                case "reread":
                {
                    var input=JsonSerializer.Deserialize<AssistantDiagnosticRead>(payload)!;
                    var(entry,settings)=await ValidateDiagnosticReadAsync(db,input);
                    target=await EnqueueIdentificationAsync(db,entry,settings,await SortingContextAsync(db,entry,settings),"assistant:"+caller.ConnectionId,true);revision=1;break;
                }
                case "forget_example":
                {
                    var input=JsonSerializer.Deserialize<AssistantExampleRemoval>(payload)!;var example=await db.ReaderTeachingExamples.SingleOrDefaultAsync(x=>x.Id==input.ExampleId)??throw new StorageException("not_found: Teaching example not found.");
                    if(example.Revision!=input.ExpectedRevision)throw new StorageException("stale_example: Read the example again.");
                    before=JsonSerializer.Serialize(new {example.Lesson,example.ChoiceJson,example.RemovedAt});
                    example.RemovedAt=time.GetUtcNow();example.Revision++;target=example.Id;revision=example.Revision;break;
                }
                default:throw new StorageException("unsupported_edit: Unknown edit action.");
            }
        return(target,revision,before);
    }
    public async Task<int> PendingAssistantEditsAsync(string actor)
    { Actor(actor);await using var db=await factory.CreateDbContextAsync();return await db.AssistantEdits.CountAsync(x=>x.Actor==actor&&x.State=="Pending"); }
    public async Task<List<AssistantEdit>> AssistantEditsAsync(string actor)
    {
        Actor(actor);await using var db=await factory.CreateDbContextAsync();return await db.AssistantEdits.AsNoTracking().Where(x=>x.Actor==actor).OrderByDescending(x=>x.CreatedAt).Take(50).ToListAsync();
    }
    public async Task SetAssistantReviewModeAsync(Guid connectionId,bool reviewFirst,string actor)
    {
        Actor(actor);await writer.WaitAsync();try
        {
            await using var db=await factory.CreateDbContextAsync();var connection=await db.AssistantConnections.SingleOrDefaultAsync(x=>x.Id==connectionId&&x.OwnerId==actor&&x.RevokedAt==null)??throw new StorageException("Connection not found.");
            connection.ReviewChangesFirst=reviewFirst;Audit(db,new(actor,connectionId),"connection.review_mode",reviewFirst?"review_first":"apply_then_verify");await db.SaveChangesAsync();
        }finally{writer.Release();}
    }
    public async Task DecideAssistantEditAsync(Guid id,string actor,bool accept,bool verify=false)
    {
        Actor(actor);await writer.WaitAsync();try
        {
            await using var db=await factory.CreateDbContextAsync();await using var tx=await db.Database.BeginTransactionAsync();
            var edit=await db.AssistantEdits.SingleOrDefaultAsync(x=>x.Id==id&&x.Actor==actor)??throw new StorageException("Change not found.");
            if(verify)
            {
                if(edit.State=="Verified")return;
                if(edit.State!="Applied")throw new StorageException("Only applied changes can be marked verified.");
                edit.State="Verified";edit.VerifiedAt=time.GetUtcNow();edit.VerifiedBy=actor;
            }
            else
            {
                if(edit.State!="Pending")throw new StorageException("This change is no longer waiting for review.");
                if(!accept)edit.State="Rejected";
                else
                {
                    var caller=new AssistantCaller(actor,edit.ConnectionId);await CheckCallerAsync(db,caller,"sorting.manage");await CheckCallerAsync(db,caller,"documents.read");
                    if(edit.CreatedAt.AddHours(24)<=time.GetUtcNow()||(await AssistantContextLockedAsync(db)).Hash!=edit.ContextHash)throw new StorageException("stale_context: Request a fresh change with current context.");
                    var(target,revision,before)=await ApplyAssistantEditLockedAsync(db,edit.Action,edit.PayloadJson,caller);
                    await db.SaveChangesAsync();edit.AfterJson=await AssistantSnapshotAsync(db,edit.Action,target);
                    edit.State="Applied";edit.BeforeJson=before;edit.ResultJson=JsonSerializer.Serialize(new AssistantEditResult(edit.Id,edit.Action,target,revision,true));
                }
            }
            Audit(db,new(actor,edit.ConnectionId),"sorting.change."+(verify?"verify":accept?"accept":"reject"),edit.State,edit.Id);
            await db.SaveChangesAsync();await tx.CommitAsync();
        }finally{writer.Release();}
    }

}
