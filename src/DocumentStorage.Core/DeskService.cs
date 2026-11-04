using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Core;

public sealed record DeskThread(DeskConversation Conversation,List<DeskTurn> Turns,List<FolderPlanApproval> Approvals,List<FolderPlanStep> Steps);
public sealed partial class LibraryService
{
    private readonly SemaphoreSlim modelWork=new(1,1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,CancellationTokenSource> deskRequests=new();
    public async Task<List<DeskConversation>> DeskConversationsAsync(string actor,string mode)
    {Actor(actor);await using var db=await factory.CreateDbContextAsync();return await db.DeskConversations.AsNoTracking().Where(x=>x.Owner==actor&&x.Mode==mode&&!x.Archived).OrderByDescending(x=>x.UpdatedAt).Take(50).ToListAsync();}
    private static async Task<DeskConversation> OwnedConversationAsync(CatalogDb db,Guid id,string actor)
    {Actor(actor);return await db.DeskConversations.SingleOrDefaultAsync(x=>x.Id==id&&x.Owner==actor)??throw new StorageException("Conversation not found for this sign-in.");}
    private static async Task<DeskTurn> OwnedTurnAsync(CatalogDb db,Guid id,string actor)
    {
        var turn=await db.DeskTurns.SingleOrDefaultAsync(x=>x.Id==id)??throw new StorageException("Message not found.");await OwnedConversationAsync(db,turn.ConversationId,actor);return turn;
    }
    public async Task<Guid> CreateDeskConversationAsync(string mode,Guid? scopeId,string actor)
    {
        Actor(actor);if(mode is not ("ask" or "folders"))throw new StorageException("Choose document questions or folder planning.");
        await writer.WaitAsync();try
        {
            await using var db=await factory.CreateDbContextAsync();
            if(scopeId is Guid id){var entry=await db.Entries.SingleOrDefaultAsync(x=>x.Id==id);if(entry==null||entry.IsMissing||entry.TrashBatchId!=null||(mode=="folders"&&!entry.IsFolder))throw new StorageException("Choose an available scope.");}
            else if(mode=="folders")throw new StorageException("Choose a parent folder for the plan.");
            if(mode=="folders")await PlanningFoldersAsync(db,scopeId!.Value);
            var conversation=new DeskConversation{Owner=actor,Mode=mode,ScopeEntryId=scopeId,Title=mode=="ask"?"New document conversation":"New folder plan",CreatedAt=time.GetUtcNow(),UpdatedAt=time.GetUtcNow()};
            db.DeskConversations.Add(conversation);await db.SaveChangesAsync();return conversation.Id;
        }finally{writer.Release();}
    }
    public async Task<DeskThread> DeskThreadAsync(Guid id,string actor)
    {
        await using var db=await factory.CreateDbContextAsync();var conversation=await OwnedConversationAsync(db,id,actor);
        var turns=await db.DeskTurns.AsNoTracking().Where(x=>x.ConversationId==id).OrderByDescending(x=>x.CreatedAt).Take(100).ToListAsync();turns.Reverse();
        var ids=turns.Select(x=>x.Id).ToArray();var approvals=await db.FolderPlanApprovals.AsNoTracking().Where(x=>ids.Contains(x.TurnId)).ToListAsync();var plans=approvals.Select(x=>x.Id).ToArray();
        return new(conversation,turns,approvals,await db.FolderPlanSteps.AsNoTracking().Where(x=>plans.Contains(x.ApprovalId)).OrderBy(x=>x.Ordinal).ToListAsync());
    }
    public async Task<Guid> SendDeskMessageAsync(Guid id,string question,string requestKey,string actor)
    {
        Actor(actor);question=question.Trim();if(question.Length is 0 or >4000||question.Contains('\0')||requestKey.Length is <8 or >100||requestKey.Contains('\0'))throw new StorageException("Write a question or request up to 4,000 characters.");
        await writer.WaitAsync();try
        {
            await using var db=await factory.CreateDbContextAsync();var conversation=await OwnedConversationAsync(db,id,actor);
            var prior=await db.DeskTurns.SingleOrDefaultAsync(x=>x.ConversationId==id&&x.RequestKey==requestKey);
            if(prior!=null){if(prior.Question!=question)throw new StorageException("That request key was used for a different message.");return prior.Id;}
            if(conversation.Archived)throw new StorageException("Start a new conversation; this one is archived.");
            var settings=await AiDefaultsAsync(db);if(!settings.Enabled)throw new StorageException("Enable your local AI reader in Settings before asking questions or drafting folders.");
            if(await db.DeskTurns.AnyAsync(x=>x.ConversationId==id&&(x.State=="Queued"||x.State=="Running")))throw new StorageException("Wait for this conversation's reply, or cancel it first.");
            if(await db.DeskTurns.CountAsync(x=>x.ConversationId==id)>=100)throw new StorageException("This conversation has 100 messages. Start a new one to continue.");
            if(await db.FolderPlanApprovals.AnyAsync(x=>db.DeskTurns.Where(t=>t.ConversationId==id).Select(t=>t.Id).Contains(x.TurnId)&&x.State=="Applying"))throw new StorageException("Finish the folder application first.");
            var turn=new DeskTurn{ConversationId=id,Question=question,RequestKey=requestKey,SettingsRevision=settings.Revision,CreatedAt=time.GetUtcNow()};
            db.DeskTurns.Add(turn);conversation.UpdatedAt=time.GetUtcNow();if(!await db.DeskTurns.AnyAsync(x=>x.ConversationId==id))conversation.Title=question[..Math.Min(70,question.Length)];
            await db.SaveChangesAsync();return turn.Id;
        }finally{writer.Release();}
    }
    public async Task CancelDeskMessageAsync(Guid id,string actor)
    {
        await writer.WaitAsync();try{await using var db=await factory.CreateDbContextAsync();var turn=await OwnedTurnAsync(db,id,actor);
            if(turn.State is "Queued" or "Running"){turn.State="Cancelled";turn.Error="Reading cancelled. Your message is saved.";await db.SaveChangesAsync();}}
        finally{writer.Release();CancelLiveDeskMessage(id);}
    }
    private void CancelLiveDeskMessage(Guid id){if(deskRequests.TryGetValue(id,out var live))try{live.Cancel();}catch(ObjectDisposedException){}}
    public async Task ArchiveDeskConversationAsync(Guid id,string actor)
    {
        await writer.WaitAsync();try{await using var db=await factory.CreateDbContextAsync();var conversation=await OwnedConversationAsync(db,id,actor);
            if(await db.DeskTurns.AnyAsync(x=>x.ConversationId==id&&(x.State=="Queued"||x.State=="Running")))throw new StorageException("Cancel or finish the active reply before archiving.");
            if(await db.FolderPlanApprovals.AnyAsync(x=>db.DeskTurns.Where(t=>t.ConversationId==id).Select(t=>t.Id).Contains(x.TurnId)&&(x.State=="Applying"||x.State=="Paused")))throw new StorageException("Resume or cancel the remaining folder steps before archiving this conversation.");
            conversation.Archived=true;await db.SaveChangesAsync();}finally{writer.Release();}
    }
    public async Task RecoverDeskAsync()
    {
        await writer.WaitAsync();try{await using var db=await factory.CreateDbContextAsync();await db.DeskTurns.Where(x=>x.State=="Running").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.State,"Failed").SetProperty(x=>x.Error,"The server restarted. Your message is saved; send it again to retry."));
            await db.FolderPlanApprovals.Where(x=>x.State=="Applying").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.State,"Paused").SetProperty(x=>x.Error,"The server restarted. Resume to finish the remaining folders."));}
        finally{writer.Release();}
    }
    public async Task<bool> ProcessNextDeskAsync(IOllamaClient client,CancellationToken token=default)
    {
        if(!await modelWork.WaitAsync(0,token))return false;
        DeskTurn? turn=null;DeskConversation? conversation=null;DeskModelRequest? input=null;CancellationTokenSource? live=null;
        try
        {
            await writer.WaitAsync(token);
            try
            {
                await using var db=await factory.CreateDbContextAsync(token);var settings=await AiDefaultsAsync(db);if(!settings.Enabled)return false;
                turn=await db.DeskTurns.Where(x=>x.State=="Queued").OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).FirstOrDefaultAsync(token);if(turn==null)return false;
                conversation=await db.DeskConversations.AsNoTracking().SingleAsync(x=>x.Id==turn.ConversationId,token);
                if(conversation.Mode=="ask"&&await db.Entries.AnyAsync(x=>!x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null&&x.TextStatus=="Indexed"&&x.PassageRevision=="",token))return false;
                if(conversation.Archived||settings.Revision!=turn.SettingsRevision){turn.State="Failed";turn.Error="Reader settings changed. Send the message again.";await db.SaveChangesAsync(token);return true;}
                var history=await db.DeskTurns.AsNoTracking().Where(x=>x.ConversationId==conversation.Id&&x.Id!=turn.Id&&(x.State=="Answered"||x.State=="Applied")).OrderByDescending(x=>x.CreatedAt).Take(3).ToListAsync(token);history.Reverse();
                var historyJson=JsonSerializer.Serialize(history.Select((x,i)=>new{question=x.Question[..Math.Min(1000,x.Question.Length)],reply=x.Reply[..Math.Min(1000,x.Reply.Length)],plan=conversation.Mode=="folders"&&i==history.Count-1?(JsonSerializer.Deserialize<DeskResult>(x.ResultJson)?.Folders??[]).Select(f=>f with {Instructions=f.Instructions[..Math.Min(200,f.Instructions.Length)]}).ToList():new List<FolderNode>()}));
                var context=conversation.Mode=="ask"?await QuestionContextAsync(db,conversation,turn.Question,string.Join(" ",history.Select(x=>x.Question))):await FolderPlanningContextAsync(db,conversation.ScopeEntryId!.Value,settings);
                turn.ContextJson=context.Json;turn.ContextHash=context.Hash;turn.State="Running";await db.SaveChangesAsync(token);
                input=new(settings.Endpoint,settings.Model,settings.TimeoutSeconds,new(conversation.Mode,turn.Question,historyJson,context.Json));
                live=CancellationTokenSource.CreateLinkedTokenSource(token);deskRequests[turn.Id]=live;
            }
            catch(StorageException ex)
            {
                if(turn==null)throw;
                await using var failed=await factory.CreateDbContextAsync(token);var saved=await failed.DeskTurns.SingleAsync(x=>x.Id==turn.Id,token);saved.State="Failed";saved.Error=ex.Message;await failed.SaveChangesAsync(token);return true;
            }
            finally{writer.Release();}
            DeskResult? result=null;string? error=null;
            try
            {
                using var evidence=JsonDocument.Parse(input.Context.EvidenceJson);
                result=conversation.Mode=="ask"&&evidence.RootElement.GetProperty("sources").GetArrayLength()==0?
                    new("I couldn’t find indexed passages that answer that question. Try a document name, a more specific phrase, or choose a narrower folder. Documents without OCR text cannot be searched yet.",true,[],[]):await client.ConverseAsync(input,live.Token);
                result=ValidateDeskResult(result,input.Context);
            }
            catch(OperationCanceledException){error="Reading stopped. Your message is saved.";}
            catch(OllamaException ex){error=ex.Message;}
            catch(Exception){error="The local reader could not finish. Your message is saved; try again.";}
            await writer.WaitAsync(token);
            try
            {
                await using var db=await factory.CreateDbContextAsync(token);var saved=await db.DeskTurns.SingleAsync(x=>x.Id==turn.Id,token);if(saved.State!="Running")return true;
                var settings=await AiDefaultsAsync(db);
                if(!settings.Enabled||settings.Revision!=saved.SettingsRevision){error="Reader settings changed during the reply. Ask again.";result=null;}
                if(result!=null&&conversation.Mode=="ask")foreach(var source in Sources(saved)){if(!await SourceCurrentAsync(db,source)){error="A retrieved source changed while the reader answered. Ask again for a current answer.";result=null;break;}}
                if(result!=null&&conversation.Mode=="folders")
                {
                    if(JsonDocument.Parse(input.Context.EvidenceJson).RootElement.GetProperty("truncated").GetBoolean()&&result.Folders.Any(x=>x.ExistingId.Length==0)){error="The folder inventory was shortened. Start a plan within a narrower parent folder.";result=null;}
                    else if((await FolderPlanningContextAsync(db,conversation.ScopeEntryId!.Value,settings)).Hash!=saved.ContextHash){error="The folder tree or instructions changed. Ask for an updated plan.";result=null;}
                    else try{await ResolveFolderPlanAsync(db,conversation.ScopeEntryId.Value,result.Folders);}catch(StorageException ex){error=ex.Message;result=null;}
                }
                saved.State=result==null?"Failed":"Answered";saved.Error=error;saved.Reply=result?.Reply??"";saved.ResultJson=result==null?"{}":JsonSerializer.Serialize(result);await db.SaveChangesAsync(token);
            }finally{writer.Release();}
            return true;
        }
        finally{if(turn!=null)deskRequests.TryRemove(turn.Id,out _);live?.Dispose();modelWork.Release();}
    }
    private static DeskResult ValidateDeskResult(DeskResult result,DeskContext input)
    {
        var json=input.Mode=="ask"?JsonSerializer.Serialize(new{reply=result.Reply,insufficient=result.Insufficient,citations=result.Citations.Select(x=>new{source_id=x.SourceId,quote=x.Quote})}):
            JsonSerializer.Serialize(new{reply=result.Reply,insufficient=result.Insufficient,folders=result.Folders.Select(x=>new{key=x.Key,parent=x.Parent,name=x.Name,instructions=x.Instructions,existing_id=x.ExistingId})});
        return OllamaClient.ParseDesk(json,input);
    }
}
