using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class AssistantCoachingChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory,string scratch,Action<bool,string> check,Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);var clock=new Clock();var library=new LibraryService(factory,new DocumentTextReader(),clock);const string actor="coaching-owner";
        var root=await library.AddRootAsync("Synthetic assistant coaching",scratch,false);var top=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.ParentId==null);
        var inbox=await library.CreateFolderAsync(top.Id,"Incoming");await library.SetIntakeAsync(inbox,true);
        async Task<CatalogEntry> Upload(string name){await library.UploadAsync(inbox,name,new MemoryStream(Encoding.UTF8.GetBytes(name=="next-policy.txt"?"Synthetic Cedar Church safety ministry. Policy reference P-28. Date 2026-11-30. Training policy.":"Synthetic Cedar Church safety ministry. Policy reference P-27. Date 2026-10-31. Training policy.")));clock.Advance();await library.ScanAsync();return (await library.BrowseAsync(inbox)).Items.Single(x=>x.Name==name);}
        var document=await Upload("policy.txt");
        var connection=new AssistantConnection {OwnerId=actor,Name="Strong synthetic assistant",ClientId=Guid.NewGuid().ToString(),ScopesJson=JsonSerializer.Serialize(new[]{"catalog.read","documents.read","sorting.manage"}),ReviewChangesFirst=true};
        await using(var db=await factory.CreateDbContextAsync()){db.AssistantConnections.Add(connection);await db.SaveChangesAsync();}
        var caller=new AssistantCaller(actor,connection.Id);
        async Task<AssistantEditResult> Edit(string action,object value,string key,bool apply=false)=>await library.EditAssistantAsync(action,JsonSerializer.Serialize(value),(await library.AssistantContextAsync()).Hash,key,"Improve synthetic policy handling.",caller,apply);
        await library.SortVocabularyAsync();
        var kind=new KindProposal(null,"Safety policy","Safety ministry training and policy documents.","Church/Safety Ministry","{title}",["policy_number"],new(){["policy_number"]=new("id","Use the printed policy reference, not a page number.",["Policy reference"])});
        var pending=await Edit("kind",new AssistantKindEdit(kind,0),"coaching-kind-01");
        check(!pending.Applied&&pending.State=="Pending"&&!(await library.SortVocabularyAsync()).Kinds.Any(x=>x.Name==kind.Name),"review-first coaching creates a durable pending change without applying it");
        await library.DecideAssistantEditAsync(pending.ReceiptId,actor,true);
        var savedKind=(await library.SortVocabularyAsync()).Kinds.Single(x=>x.Name==kind.Name);
        check(!savedKind.AutoFile&&LibraryService.FieldGuides(savedKind)["policy_number"].Look.Contains("printed policy reference"),"accepting training sets narrow extraction guidance without enabling automatic filing");
        await library.SetAssistantReviewModeAsync(connection.Id,false,actor);
        var details=new AssistantDetailsEdit(new(document.Id,kind.Name,"Cedar Church","","Volunteer policy",["training","policy"],new(){["policy_number"]="P-27"}),0,document.ContentHash);
        var hash=(await library.AssistantContextAsync()).Hash;var payload=JsonSerializer.Serialize(details);
        var applied=await library.EditAssistantAsync("details",payload,hash,"coaching-details-01","Correct this synthetic policy.",caller);
        var again=await library.EditAssistantAsync("details",payload,hash,"coaching-details-01","Correct this synthetic policy.",caller);
        check(applied.Applied&&again.ReceiptId==applied.ReceiptId&&(await library.DocumentDetailsAsync(document.Id))!.Revision==1,"apply-then-verify saves corrections once and retries return the durable receipt");
        await using(var db=await factory.CreateDbContextAsync())check((await db.Details.SingleAsync(x=>x.EntryId==document.Id)).SourcesJson.Contains("assistant")&&!await db.Reviews.AnyAsync(x=>x.EntryId==document.Id&&x.Reviewed),"assistant corrections retain assistant provenance and never mark facts human reviewed");
        await library.DecideAssistantEditAsync(applied.ReceiptId,actor,true,true);
        check((await library.AssistantEditsAsync(actor)).Single(x=>x.Id==applied.ReceiptId).State=="Verified","verification records a checked decision separately from applying it");
        await refuses(()=>library.EditAssistantAsync("details",payload,hash,"coaching-details-01","Different intent.",caller),"idempotency keys cannot be reused for a different edit");
        await refuses(()=>Edit("details",details,"coaching-details-stale"),"stale document details revision cannot overwrite a correction");
        await refuses(()=>library.EditAssistantAsync("details",payload,hash,"coaching-wrong-connection","synthetic",new(actor,Guid.NewGuid())),"unregistered or wrong connections cannot mutate metadata");
        var teach=await Edit("example",new AssistantExampleEdit(document.Id,1,document.ContentHash,"Use the printed policy reference, not a page number."),"coaching-example-01");
        check(teach.Applied&&File.Exists(Path.Combine(scratch,"Incoming/policy.txt")),"teaching saves a source-bound example and leaves source bytes in place");
        var next=await Upload("next-policy.txt");var settings=(await library.IdentificationOverviewAsync()).Settings;settings.Enabled=true;settings.AutoIdentify=false;settings.Model="synthetic-coach:latest";await library.SaveOllamaSettingsAsync(settings,settings.Revision,actor);
        var previousAutoFiling=await library.AutoFilingEnabledAsync();
        await library.SetKindAutoFileAsync(savedKind.Id,true,actor);await library.SetAutoFilingEnabledAsync(true,actor);
        var diagnostic=await Edit("reread",new AssistantDiagnosticRead(next.Id,next.ContentHash),"coaching-diagnostic-01");
        await refuses(()=>Edit("reread",new AssistantDiagnosticRead(next.Id,next.ContentHash),"coaching-diagnostic-busy"),"diagnostic requests do not start a second active reading");
        var sawLesson=false;
        await library.ProcessNextIdentificationAsync(new ScriptedModel(input=>{sawLesson|=input.Step=="details"&&input.Question.Contains("printed policy reference")&&input.Question.Contains("policy_number = P-27");return new(kind.Name,"","Cedar Church","2026-11-30","Volunteer policy",["policy"],[new("policy_number","P-28","Policy reference P-28")],"high");}));
        check(sawLesson,"future narrow local-reader requests include the corrected teaching example and field guide");
        var diagnosticCard=(await library.SortCardAsync(next.Id))!;
        check(diagnosticCard.Sure&&!diagnosticCard.Duplicate,"the diagnostic fixture is otherwise eligible for trusted automatic filing");
        check(!await library.TryAutoFileAsync(diagnostic.TargetId)&&File.Exists(Path.Combine(scratch,"Incoming/next-policy.txt")),"assistant diagnostic readings never auto-file, even when kind and master trust are on");
        var diagnosticAgain=await Edit("reread",new AssistantDiagnosticRead(next.Id,next.ContentHash),"coaching-diagnostic-01");
        check(diagnosticAgain.ReceiptId==diagnostic.ReceiptId,"retrying a completed diagnostic request reuses its saved attempt rather than consuming inference again");
        await library.SetKindAutoFileAsync(savedKind.Id,false,actor);await library.SetAutoFilingEnabledAsync(previousAutoFiling,actor);
        var forgotten=await Edit("forget_example",new AssistantExampleRemoval(teach.TargetId,1),"coaching-forget-01");
        await using(var db=await factory.CreateDbContextAsync())check((await db.ReaderTeachingExamples.SingleAsync(x=>x.Id==teach.TargetId)).RemovedAt!=null&&forgotten.Applied,"forgetting removes teaching influence while retaining its history");
        await library.SetAssistantReviewModeAsync(connection.Id,true,actor);
        var directOverride=await Edit("tag",new AssistantNameEdit(null,"church-safety",[],0),"coaching-override-01",true);
        check(directOverride.Applied,"an explicit go-ahead applies a change despite the connection's review-first default");
        var pendingTag=await Edit("tag",new AssistantNameEdit(null,"ministry-training",[],0),"coaching-tag-pending");
        await library.DecideAssistantEditAsync(pendingTag.ReceiptId,actor,false);
        check(!(await library.SortVocabularyAsync()).Tags.Any(x=>x.Name=="ministry-training"),"declining a pending change leaves vocabulary untouched");
        await using(var db=await factory.CreateDbContextAsync()){(await db.AssistantConnections.SingleAsync(x=>x.Id==connection.Id)).RevokedAt=clock.GetUtcNow();await db.SaveChangesAsync();}
        await refuses(()=>Edit("tag",new AssistantNameEdit(null,"revoked-tag",[],0),"coaching-revoked-01",true),"revocation is rechecked under the writer before applying edits");
        settings=(await library.IdentificationOverviewAsync()).Settings;settings.Enabled=false;await library.SaveOllamaSettingsAsync(settings,settings.Revision,actor);await library.SetIntakeAsync(inbox,false);
    }
    private sealed class Clock:TimeProvider {private DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;public void Advance()=>now=now.AddSeconds(6);}
}
