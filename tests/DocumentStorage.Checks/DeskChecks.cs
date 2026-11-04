using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

internal static class DeskChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory,string scratch,Action<bool,string> check,Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);var library=new LibraryService(factory,new DocumentTextReader());const string actor="desk-owner";
        var rootId=await library.AddRootAsync("Synthetic conversation archive",scratch,false);var root=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==rootId&&x.ParentId==null);
        var finance=await library.CreateFolderAsync(root.Id,"Finance");var church=await library.CreateFolderAsync(root.Id,"Cedar Churhc");
        await library.UploadAsync(finance,"equity.txt",new MemoryStream(Encoding.UTF8.GetBytes("Synthetic home equity agreement. The interest rate is 5.25 percent. No facts here are real.")));
        var equity=(await library.BrowseAsync(finance)).Items.Single(x=>x.Name=="equity.txt");
        var settings=(await library.IdentificationOverviewAsync()).Settings;settings.Enabled=true;settings.AutoIdentify=false;settings.Model="synthetic-desk";await library.SaveOllamaSettingsAsync(settings,settings.Revision,actor);
        while(await library.IndexNextPassagesAsync()){}
        await using(var db=await factory.CreateDbContextAsync())check(await db.DocumentPassages.AnyAsync(x=>x.EntryId==equity.Id)&& (await library.PassageStatusAsync()).Pending==0,"existing indexed documents gain searchable passages without re-OCR or moving their bytes");
        var split=LibraryService.SplitPassages(new(){Name="synthetic.pdf",ExtractedText="Synthetic cover page\fThe rate on the second page is 5.25 percent.\f"});
        check(split.Count==2&&split[1].Page==2&&split[1].Start==21,"PDF form-feed boundaries preserve the actual page and source offset");
        var emoji=LibraryService.SplitPassages(new(){Name="unicode.txt",ExtractedText=new string('a',1399)+"🙂"+new string('b',2000)});
        check(emoji.All(x=>!char.IsLowSurrogate(x.Text[0])&&!char.IsHighSurrogate(x.Text[^1])),"passage boundaries do not split Unicode surrogate pairs");
        var conversation=await library.CreateDeskConversationAsync("ask",finance,actor);
        await refuses(()=>library.DeskThreadAsync(conversation,"another-owner"),"conversations and saved source text belong to the actual sign-in");
        var request=await library.SendDeskMessageAsync(conversation,"What interest rate is on my home equity agreement?","desk-question-one",actor);
        check(await library.SendDeskMessageAsync(conversation,"What interest rate is on my home equity agreement?","desk-question-one",actor)==request,"repeated message requests return the same durable turn");
        await refuses(()=>library.SendDeskMessageAsync(conversation,"Different message","desk-question-one",actor),"request keys cannot be reused for different text");
        DeskModelRequest? captured=null;
        DeskResult Answer(DeskModelRequest input)
        {
            var sources=JsonSerializer.Deserialize<List<DeskSource>>(JsonDocument.Parse(input.Context.EvidenceJson).RootElement.GetProperty("sources").GetRawText())!;
            var source=sources.First(x=>x.EntryId==equity.Id);return new("The agreement lists 5.25 percent ["+source.Id+"].",false,[new(source.Id,"The interest rate is 5.25 percent.")],[]);
        }
        await library.ProcessNextDeskAsync(new Model(input=>{captured=input;return Task.FromResult(Answer(input));}));
        var answered=(await library.DeskThreadAsync(conversation,actor)).Turns.Single();
        check(answered.State=="Answered"&&answered.Reply.Contains("5.25 percent")&&captured!=null&&!captured.Context.EvidenceJson.Contains(scratch),"ranked local passages produce a saved answer without exposing physical server paths");
        var result=JsonSerializer.Deserialize<DeskResult>(answered.ResultJson)!;
        foreach(var mode in new[]{"ask","folders"})
        {
            var httpCalls=0;using var http=new HttpClient(new RemoteMetadata(()=>httpCalls++));var rejected=false;
            try{await new OllamaClient(http).ConverseAsync(captured! with {Context=captured!.Context with {Mode=mode}});}catch(OllamaException){rejected=true;}
            check(rejected&&httpCalls==1,"cloud-backed "+mode+" models are refused before sending source text or folder context");
        }
        check((await library.OpenDeskSourceAsync(answered.Id,result.Citations.Single().SourceId,actor)).Id==equity.Id,"each validated citation opens its original stable document identity");
        foreach(var wire in new[]{"{\"reply\":\"Made up [S999]\",\"insufficient\":false,\"citations\":[{\"source_id\":\"S999\",\"quote\":\"Not a real quote\"}]}","{\"reply\":\"Made up [S1]\",\"insufficient\":false,\"citations\":[{\"source_id\":\"S1\",\"quote\":\"The rate is ninety percent\"}]}","{\"reply\":\"Made up answer\",\"insufficient\":false,\"citations\":[]}"})
        {var rejected=false;try{OllamaClient.ParseDesk(wire,captured!.Context);}catch(OllamaException){rejected=true;}check(rejected,"answers reject invented source IDs, unsupported quotes or uncited factual replies");}
        await library.SendDeskMessageAsync(conversation,"Is that the interest rate?","desk-followup-one",actor);
        await library.ProcessNextDeskAsync(new Model(input=>{check(input.Context.HistoryJson.Contains("5.25 percent")&&input.Context.HistoryJson.Contains("home equity"),"follow-up questions receive bounded saved conversation context");return Task.FromResult(Answer(input));}));
        await library.SaveInstructionsAsync(church,"Church ministry documents belong here.",actor);
        var planThread=await library.CreateDeskConversationAsync("folders",church,actor);
        var planTurn=await library.SendDeskMessageAsync(planThread,"Create a safety ministry folder with training inside it.","desk-plan-one",actor);
        var nodes=new List<FolderNode>{new("F1",church.ToString(),"Safety Ministry","Church safety guides and post positions belong here.",""),new("F2","F1","Training","Volunteer training documents belong here.","")};
        await library.ProcessNextDeskAsync(new Model(input=>{check(input.Context.Mode=="folders"&&input.Context.EvidenceJson.Contains("Cedar Churhc")&&input.Context.EvidenceJson.Contains("Church ministry")&&!input.Context.EvidenceJson.Contains(scratch),"folder planning sees scoped real folders and their instructions without absolute paths");return Task.FromResult(new DeskResult("A safety ministry folder with a training child.",false,[],nodes));}));
        check(!Directory.Exists(Path.Combine(scratch,"Cedar Churhc/Safety Ministry")),"conversational folder proposals do not create folders before review");
        var preview=await library.PreviewFolderPlanAsync(planTurn,nodes,actor);
        await refuses(()=>library.ApplyFolderPlanAsync(preview.Id,"another-owner"),"folder-plan approval is bound to its actual actor");
        await library.ApplyFolderPlanAsync(preview.Id,actor);await library.ApplyFolderPlanAsync(preview.Id,actor);
        var plan=await library.DeskThreadAsync(planThread,actor);var created=plan.Steps.Single(x=>x.Name=="Safety Ministry");
        check(Directory.Exists(Path.Combine(scratch,"Cedar Churhc/Safety Ministry/Training"))&&plan.Approvals.Single().State=="Applied"&&plan.Steps.All(x=>x.State=="Completed"),"one reviewed plan creates the nested real folder tree with idempotent completion");
        var notes=await library.InstructionThreadAsync(new("folder",created.FolderId.ToString()));
        check(notes.Text.Contains("post positions")&&notes.History.Single().SourceMessagesJson.Contains(notes.Messages.Single().Id.ToString()),"planned filing guidance is saved with immutable versions and its original conversational source request");
        check(!Directory.EnumerateFiles(Path.Combine(scratch,"Cedar Churhc/Safety Ministry"),".documentstorage*",SearchOption.AllDirectories).Any(),"completed creation cleans up internal ownership markers");
        await library.SendDeskMessageAsync(planThread,"What should we add next?","desk-applied-followup",actor);
        await library.ProcessNextDeskAsync(new Model(input=>{check(input.Context.HistoryJson.Contains("Safety Ministry"),"follow-up planning retains the prior conversation and plan even after folders were applied");return Task.FromResult(new DeskResult("What else would you like to store?",true,[],[]));}));
        // Reuse is explicit and cannot rename existing folders or cross the chosen scope.
        async Task<(Guid Thread,Guid Turn)> Proposal(List<FolderNode> draft)
        {var id=await library.CreateDeskConversationAsync("folders",church,actor);var turn=await library.SendDeskMessageAsync(id,"Synthetic revised folder organization","desk-plan-"+Guid.NewGuid().ToString("N"),actor);await library.ProcessNextDeskAsync(new Model(_=>Task.FromResult(new DeskResult("Synthetic plan.",false,[],draft))));return(id,turn);}
        var reuseNodes=new List<FolderNode>{new("F1",church.ToString(),"Safety Ministry","Updated approved safety filing guidance.",created.FolderId.ToString())};
        var reuse=await Proposal(reuseNodes);var reusePlan=await library.PreviewFolderPlanAsync(reuse.Turn,reuseNodes,actor);await library.ApplyFolderPlanAsync(reusePlan.Id,actor);
        check((await library.EntryAsync(created.FolderId))!.Instructions.StartsWith("Updated approved"),"an existing folder can receive reviewed instructions while keeping its name, path and ID");
        var draftNodes=new List<FolderNode>{new("F1",church.ToString(),"Incidents","Incident records.","")};
        var stale=await Proposal(draftNodes);await library.SaveInstructionsAsync(church,"Changed guidance after generation.",actor);
        await refuses(()=>library.PreviewFolderPlanAsync(stale.Turn,draftNodes,actor),"a changed folder tree or guidance requires a refreshed plan");
        var invalid=await Proposal([new("F1",finance.ToString(),"Escape","Outside scope.","")]);
        check((await library.DeskThreadAsync(invalid.Thread,actor)).Turns.Single().State=="Failed","model folder plans cannot escape the selected parent subtree");
        invalid=await Proposal([new("F1",church.ToString(),"../escape","Unsafe.","")]);check((await library.DeskThreadAsync(invalid.Thread,actor)).Turns.Single().State=="Failed","model folder names cannot become path traversal");
        var superseded=await Proposal([new("F1",church.ToString(),"Superseded","Old preference.","")]);var supersededPlan=await library.PreviewFolderPlanAsync(superseded.Turn,[new("F1",church.ToString(),"Superseded","Old preference.","")],actor);
        await library.SendDeskMessageAsync(superseded.Thread,"Change the earlier preference before creating anything.","desk-newer-preference",actor);
        await refuses(()=>library.ApplyFolderPlanAsync(supersededPlan.Id,actor),"a newer conversational request invalidates an already reviewed older folder plan");
        await library.ProcessNextDeskAsync(new Model(_=>Task.FromResult(new DeskResult("What name do you prefer?",true,[],[]))));
        // Pause after the directory move and before its catalog transaction. Scans must not steal its identity.
        var crash=await Proposal(draftNodes);var crashPlan=await library.PreviewFolderPlanAsync(crash.Turn,draftNodes,actor);
        await using(var db=await factory.CreateDbContextAsync())
        {
            var options=new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(new FailFolderCommit()).Options;
            var failing=new LibraryService(new Factory(options),new DocumentTextReader());var failed=false;
            try{await failing.ApplyFolderPlanAsync(crashPlan.Id,actor);}catch(InvalidOperationException ex)when(ex.Message=="Synthetic folder commit failure"){failed=true;}
            check(failed&&Directory.Exists(Path.Combine(scratch,"Cedar Churhc/Incidents")),"an interrupted planned folder retains its real directory and durable ownership proof");
        }
        await library.ScanAsync();
        check(!(await library.OverviewAsync()).Folders.Any(x=>x.RootId==rootId&&x.RelativePath=="Cedar Churhc/Incidents"),"scanning does not adopt a folder that belongs to an interrupted creation step");
        await library.ApplyFolderPlanAsync(crashPlan.Id,actor);
        check((await library.DeskThreadAsync(crash.Thread,actor)).Approvals.Single().State=="Applied","an interrupted plan can resume without restarting the app, adopting only its proven directory and original identity");
        var collisionNodes=new List<FolderNode>{new("F1",church.ToString(),"Late collision","Never overwrite.","")};var collision=await Proposal(collisionNodes);var collisionPlan=await library.PreviewFolderPlanAsync(collision.Turn,collisionNodes,actor);
        Directory.CreateDirectory(Path.Combine(scratch,"Cedar Churhc/Late collision"));await File.WriteAllTextAsync(Path.Combine(scratch,"Cedar Churhc/Late collision/survivor.txt"),"Synthetic existing content.");
        await refuses(()=>library.ApplyFolderPlanAsync(collisionPlan.Id,actor),"a directory appearing after review pauses folder creation without adopting or replacing it");
        await library.CancelFolderPlanAsync(collisionPlan.Id,actor);await library.ScanAsync();
        check(await File.ReadAllTextAsync(Path.Combine(scratch,"Cedar Churhc/Late collision/survivor.txt"))=="Synthetic existing content.","cancelling remaining folder steps preserves unrelated directory contents");
        // Cancellation and late replies share the global inference slot with reading.
        var cancelThread=await library.CreateDeskConversationAsync("ask",equity.Id,actor);var cancelTurn=await library.SendDeskMessageAsync(cancelThread,"What is the interest rate?","desk-cancel-one",actor);
        while(await library.IndexNextPassagesAsync()){}
        var started=new TaskCompletionSource();var completion=new TaskCompletionSource<DeskResult>();
        var running=library.ProcessNextDeskAsync(new Model(input=>{captured=input;started.SetResult();return completion.Task;}));await started.Task;
        check(!await library.ProcessNextIdentificationAsync(new Model(_=>throw new Exception("Not called"))),"conversations and document identification cannot run competing Ollama jobs");
        await library.CancelDeskMessageAsync(cancelTurn,actor);completion.SetResult(Answer(captured!));await running;
        check((await library.DeskThreadAsync(cancelThread,actor)).Turns.Single().State=="Cancelled","a late local reply cannot revive a cancelled conversation message");
        var settingsThread=await library.CreateDeskConversationAsync("ask",equity.Id,actor);await library.SendDeskMessageAsync(settingsThread,"What interest rate is listed?","desk-settings-active",actor);
        var waitingThread=await library.CreateDeskConversationAsync("ask",equity.Id,actor);await library.SendDeskMessageAsync(waitingThread,"What agreement is this?","desk-settings-waiting",actor);
        var settingsStarted=new TaskCompletionSource();var settingsReply=new TaskCompletionSource<DeskResult>();CancellationToken liveToken=default;
        var changing=library.ProcessNextDeskAsync(new Model(input=>{captured=input;settingsStarted.SetResult();return settingsReply.Task;},token=>liveToken=token));await settingsStarted.Task;
        settings=(await library.IdentificationOverviewAsync()).Settings;settings.Model="synthetic-desk-revised";await library.SaveOllamaSettingsAsync(settings,settings.Revision,actor);
        settingsReply.SetResult(Answer(captured!));await changing;
        check(liveToken.IsCancellationRequested&&(await library.DeskThreadAsync(settingsThread,actor)).Turns.Single().State=="Cancelled"&&(await library.DeskThreadAsync(waitingThread,actor)).Turns.Single().State=="Cancelled","changing reader settings cancels running and queued conversations and signals the active Ollama request");
        var missingThread=await library.CreateDeskConversationAsync("ask",church,actor);await library.SendDeskMessageAsync(missingThread,"Does an orbital telescope appear in these documents?","desk-no-evidence",actor);
        await library.ProcessNextDeskAsync(new Model(_=>throw new Exception("No model call needed")));
        check((await library.DeskThreadAsync(missingThread,actor)).Turns.Single().Reply.Contains("couldn’t find indexed passages"),"questions without retrieved evidence receive a useful no-evidence reply without inventing an answer");
        await library.ArchiveDeskConversationAsync(missingThread,actor);check(!(await library.DeskConversationsAsync(actor,"ask")).Any(x=>x.Id==missingThread),"conversations can be archived without deleting document files");
        await library.MoveAsync(equity.Id,finance,"renamed-equity.txt");
        while(await library.IndexNextPassagesAsync()){}
        check((await library.OpenDeskSourceAsync(answered.Id,result.Citations.Single().SourceId,actor)).Name=="renamed-equity.txt","source links follow stable document IDs through app-managed moves when the indexed evidence is unchanged");
        await File.AppendAllTextAsync(Path.Combine(scratch,"Finance/renamed-equity.txt")," Changed synthetic agreement.");await library.ScanAsync();while(await library.IndexNextPassagesAsync()){}
        await refuses(()=>library.OpenDeskSourceAsync(answered.Id,result.Citations.Single().SourceId,actor),"old source links reject changed indexed evidence instead of silently substituting a different document version");
    }
    private sealed class Model(Func<DeskModelRequest,Task<DeskResult>> reply,Action<CancellationToken>? observe=null):IOllamaClient
    {public Task<List<LocalModel>> ModelsAsync(string endpoint,CancellationToken cancellationToken=default)=>Task.FromResult(new List<LocalModel>());public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request,CancellationToken cancellationToken=default)=>throw new NotImplementedException();public Task<DeskResult> ConverseAsync(DeskModelRequest request,CancellationToken cancellationToken=default){observe?.Invoke(cancellationToken);return reply(request);}}
    private sealed class Factory(DbContextOptions<CatalogDb> options):IDbContextFactory<CatalogDb>{public CatalogDb CreateDbContext()=>new(options);}
    private sealed class RemoteMetadata(Action called):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken token){called();return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"remote_host\":\"https://example.invalid\",\"capabilities\":[\"completion\"]}")});}}
    private sealed class FailFolderCommit:SaveChangesInterceptor
    {public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken token=default){if(data.Context!.ChangeTracker.Entries<FolderPlanStep>().Any(x=>x.Entity.State=="Completed"&&x.State==EntityState.Modified))throw new InvalidOperationException("Synthetic folder commit failure");return ValueTask.FromResult(result);}}
}
