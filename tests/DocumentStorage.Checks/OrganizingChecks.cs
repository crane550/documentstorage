using System.Text;
using System.Data.Common;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

internal static class OrganizingChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory,string scratch,Action<bool,string> check,Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);var clock=new Clock();var library=new LibraryService(factory,new DocumentTextReader(),clock);const string actor="organizing-owner";
        var root=await library.AddRootAsync("Synthetic organizing archive",scratch,false);var top=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.ParentId==null);
        var inbox=await library.CreateFolderAsync(top.Id,"Incoming");var church=await library.CreateFolderAsync(top.Id,"Cedar Churhc");var safety=await library.CreateFolderAsync(church,"Safety Minstry");
        var finance=await library.CreateFolderAsync(top.Id,"Finance");await library.SetIntakeAsync(inbox,true);await library.SaveInstructionsAsync(safety,"Ministry guides and campus volunteer documents belong here.");
        await library.SaveInstructionsAsync(church,"Shared church guidance. "+new string('c',700));
        var settings=(await library.IdentificationOverviewAsync()).Settings;settings.Enabled=true;settings.Model="synthetic-organizer";settings=await library.SaveOllamaSettingsAsync(settings,settings.Revision,actor);
        var count=0;
        async Task<CatalogEntry> New(string text="Synthetic West campus ministry post guide. Revision five.")
        {var name="source-"+(++count)+".txt";await library.UploadAsync(inbox,name,new MemoryStream(Encoding.UTF8.GetBytes(text)));clock.Advance(6);await library.ScanAsync();return (await library.BrowseAsync(inbox)).Items.Single(x=>x.Name==name);}
        async Task<IdentificationJob> Job(Guid id) {await using var db=await factory.CreateDbContextAsync();return await db.IdentificationJobs.AsNoTracking().SingleAsync(x=>x.Id==id);}
        IdentificationResult Good(IdentificationRequest input,Guid? folder=null,string create="")=>OllamaClient.Parse(JsonSerializer.Serialize(new {document_type="ministry_post_guide",needs_review=false,summary="Synthetic safety ministry guide.",facts=new[]{new{name="campus",value="West",evidence="Synthetic West campus"}},filing=new{destination_folder_id=(folder??safety).ToString(),new_folder=create,filename="West guide - Rev 5.txt",reason="The existing safety ministry folder matches the document."}}),input);
        async Task<(CatalogEntry Entry,IdentificationJob Job)> Proposed(Guid? dest=null,string create="")
        {
            var entry=await New();var id=await library.QueueOrganizationAsync(entry.Id,actor);
            await library.ProcessNextIdentificationAsync(new Model((input,_)=>Task.FromResult(Good(input,dest,create))));return(entry,await Job(id));
        }
        var initialRuleCount=(await library.RulesAsync()).Count;
        var entry=await New();var jobId=await library.QueueOrganizationAsync(entry.Id,actor);IdentificationRequest? captured=null;
        await library.ProcessNextIdentificationAsync(new Model((input,_)=>
        {
            captured=input;using var context=JsonDocument.Parse(input.ContextJson);
            check(input.Organizing&&context.RootElement.GetProperty("folders").EnumerateArray().Any(x=>x.GetProperty("path").GetString()!.Contains("Safety Minstry")),"organizing sees real destination IDs, imperfect folder spellings and destination guidance");
            check(context.RootElement.GetProperty("folders").EnumerateArray().Single(x=>x.GetProperty("id").GetGuid()==safety).GetProperty("guidance").GetString()!.StartsWith("Ministry guides and campus volunteer"),"specific destination instructions stay visible even when inherited notes exceed the context budget");
            check(!input.ContextJson.Contains(scratch)&&!context.RootElement.GetProperty("folders").EnumerateArray().Any(x=>x.GetProperty("id").GetGuid()==inbox),"organizing context excludes server paths and intake destinations");
            return Task.FromResult(Good(input));
        }));
        var job=await Job(jobId);check(job.State=="Succeeded"&&job.DocumentType=="ministry_post_guide"&&job.FactsJson.Contains("campus"),"a novel document category and useful fact need no configured type or fact profile");
        check(File.Exists(Path.Combine(scratch,"Incoming",entry.Name))&&(await library.RulesAsync()).Count==initialRuleCount,"AI proposals neither move documents nor manufacture filing rules");
        foreach(var bad in new[]{"extra_command","foreign_folder","intake_folder","traversal","extension","reserved_fact"})
        {
            var wire=new Dictionary<string,object> { ["document_type"]="ministry_guide",["needs_review"]=false,["summary"]="Synthetic",["facts"]=Array.Empty<object>(),["filing"]=new {destination_folder_id=(bad=="foreign_folder"?Guid.NewGuid():bad=="intake_folder"?inbox:safety).ToString(),new_folder=bad=="traversal"?"../escape":"",filename=bad=="extension"?"guide.exe":"guide.txt",reason="Synthetic folder match."} };
            if(bad=="extra_command")wire["execute"]="move";
            if(bad=="reserved_fact")wire["facts"]=new[]{new{name="command",value="execute",evidence="Synthetic West campus"}};
            var rejected=false;try{OllamaClient.Parse(JsonSerializer.Serialize(wire),captured!);}catch(OllamaException){rejected=true;}
            check(rejected,"organizing schema rejects unsafe output: "+bad);
        }
        OrganizationChoice Choice(Guid? folder=null,string name="Approved West guide.txt",bool remember=true,string create="")=>new(folder??safety,create,name,"ministry_post_guide","Keep campus ministry guides in the existing safety folder.",remember);
        var second=await Proposed();
        var plan=await library.PreviewOrganizationFilingAsync(job.Id,Choice(),actor);
        await refuses(()=>library.ApplyOrganizationFilingAsync(plan.Id,"another-actor"),"another sign-in cannot use an organizing approval");
        var opId=await library.ApplyOrganizationFilingAsync(plan.Id,actor);
        check(await File.ReadAllTextAsync(Path.Combine(scratch,"Cedar Churhc/Safety Minstry/Approved West guide.txt"))=="Synthetic West campus ministry post guide. Revision five."&&(await library.EntryAsync(entry.Id))!.ParentId==safety,"one approved plan files unchanged bytes to the existing folder without correcting its spelling or creating a rule");
        check(await library.ApplyOrganizationFilingAsync(plan.Id,actor)==opId,"completed organizing approvals are idempotent");
        await using(var db=await factory.CreateDbContextAsync())check(!await db.Reviews.AnyAsync(x=>x.EntryId==entry.Id)&&await db.FilingExamples.AnyAsync(x=>x.Id==plan.Id&&x.Filename=="Approved West guide.txt"),"filing approval remembers the corrected choice without falsely marking AI facts human-reviewed");
        var secondPlan=await library.PreviewOrganizationFilingAsync(second.Job.Id,Choice(name:"Second approved.txt",remember:false),actor);
        await library.ApplyOrganizationFilingAsync(secondPlan.Id,actor);
        check((await library.FilingExamplesAsync()).Count==1,"new examples do not invalidate pending proposals and remembering can be disabled per filing");
        var learned=await New();var learnedJob=await library.QueueOrganizationAsync(learned.Id,actor);
        await library.ProcessNextIdentificationAsync(new Model((input,_)=>
        {check(input.ContextJson.Contains("Approved West guide.txt")&&input.ContextJson.Contains("Keep campus ministry"),"subsequent organizing requests retrieve approved corrections as examples");return Task.FromResult(Good(input));}));
        await library.ForgetFilingExampleAsync(plan.Id,actor);
        await refuses(()=>library.PreviewOrganizationFilingAsync(learnedJob,Choice(name:"Forgotten context.txt"),actor),"forgetting a remembered example invalidates proposals that used the old memory policy");
        check((await library.FilingExamplesAsync()).Count==0,"forgotten examples leave active learning context while filing history remains intact");

        var fresh=await Proposed(create:"Training");var newPlan=await library.PreviewOrganizationFilingAsync(fresh.Job.Id,Choice(name:"Training guide.txt",create:"Training"),actor);
        await library.ApplyOrganizationFilingAsync(newPlan.Id,actor);
        check(File.Exists(Path.Combine(scratch,"Cedar Churhc/Safety Minstry/Training/Training guide.txt"))&&(await library.EntryAsync(fresh.Entry.Id))!.ParentId==newPlan.TargetFolderId,"approval can create one named subfolder and file the document through the journal");
        var existing=await Proposed();
        var reused=await library.PreviewOrganizationFilingAsync(existing.Job.Id,Choice(name:"Existing training.txt",create:"training"),actor);
        check(reused.NewFolderName==""&&reused.TargetFolderId==newPlan.TargetFolderId,"a proposed subfolder reuses an existing case-insensitive match rather than duplicating it");
        await library.RejectOrganizationAsync(existing.Job.Id,actor);
        await refuses(()=>library.ApplyOrganizationFilingAsync(reused.Id,actor),"declining a suggestion invalidates its unused filing previews");

        var changed=await Proposed();var changedPlan=await library.PreviewOrganizationFilingAsync(changed.Job.Id,Choice(name:"Never moved.txt"),actor);
        var source=Path.Combine(scratch,"Incoming",changed.Entry.Name);var timestamp=File.GetLastWriteTimeUtc(source);await File.WriteAllTextAsync(source,new string('X',(int)new FileInfo(source).Length));File.SetLastWriteTimeUtc(source,timestamp);
        await refuses(()=>library.ApplyOrganizationFilingAsync(changedPlan.Id,actor),"same-size same-timestamp changes invalidate an organizing approval by fingerprint");
        check(File.Exists(source)&&!File.Exists(Path.Combine(scratch,"Cedar Churhc/Safety Minstry/Never moved.txt")),"stale approval leaves the document in intake");
        changed=await Proposed();changedPlan=await library.PreviewOrganizationFilingAsync(changed.Job.Id,Choice(name:"Race refused.txt"),actor);
        var raceSource=Path.Combine(scratch,"Incoming",changed.Entry.Name);var race=new ChangeBeforeSnapshot(raceSource);
        await using(var db=await factory.CreateDbContextAsync())
        {
            var options=new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(race).Options;
            var racing=new LibraryService(new Factory(options),new DocumentTextReader(),clock);
            await refuses(()=>racing.ApplyOrganizationFilingAsync(changedPlan.Id,actor),"a source edit between approval revalidation and journal snapshot cannot become the approved baseline");
        }
        check(race.Changed&&File.Exists(raceSource)&&!File.Exists(Path.Combine(scratch,"Cedar Churhc/Safety Minstry/Race refused.txt")),"the final snapshot guard preserves changed bytes in intake without starting a move");
        changed=await Proposed();changedPlan=await library.PreviewOrganizationFilingAsync(changed.Job.Id,Choice(name:"Guidance stale.txt"),actor);await library.SaveInstructionsAsync(safety,new string('a',700)+" policy changed beyond transmitted guidance");
        await refuses(()=>library.ApplyOrganizationFilingAsync(changedPlan.Id,actor),"destination guidance edits beyond the prompt excerpt invalidate approvals");
        await library.SaveInstructionsAsync(safety,"Ministry guides belong here.");
        changed=await Proposed();changedPlan=await library.PreviewOrganizationFilingAsync(changed.Job.Id,Choice(name:"Expired.txt"),actor);clock.Advance(901);
        await refuses(()=>library.ApplyOrganizationFilingAsync(changedPlan.Id,actor),"expired organizing previews cannot move documents");
        changed=await Proposed();await File.WriteAllTextAsync(Path.Combine(scratch,"Cedar Churhc/Safety Minstry/Collision.txt"),"Existing original.");
        await refuses(()=>library.PreviewOrganizationFilingAsync(changed.Job.Id,Choice(name:"Collision.txt"),actor),"organizing previews reject existing filenames without overwriting them");
        await refuses(()=>library.PreviewOrganizationFilingAsync(changed.Job.Id,Choice(folder:inbox),actor),"corrected plans cannot file back inside intake");
        var foreignPath=Path.Combine(scratch,"other-root"); // Register a non-overlapping sibling, not this nested directory.
        foreignPath=scratch+"-other";Directory.CreateDirectory(foreignPath);var foreignRoot=await library.AddRootAsync("Other organizer storage",foreignPath,false);
        var foreign=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==foreignRoot&&x.ParentId==null);
        await refuses(()=>library.PreviewOrganizationFilingAsync(changed.Job.Id,Choice(folder:foreign.Id),actor),"human corrections cannot bypass the same-storage transfer boundary");

        var pending=await New();var pendingId=await library.QueueOrganizationAsync(pending.Id,actor);var started=new TaskCompletionSource();var completion=new TaskCompletionSource<IdentificationResult>();
        var processing=library.ProcessNextIdentificationAsync(new Model((input,_)=>{started.SetResult();return completion.Task;}));await started.Task;
        check(!await library.ProcessNextIdentificationAsync(new Model((input,_)=>Task.FromResult(Good(input)))),"organizing and legacy identification share the existing one-document-at-a-time queue");
        await library.CancelIdentificationAsync(pendingId,actor);completion.SetResult(Good(captured!));await processing;check((await Job(pendingId)).State=="Cancelled","late model replies cannot revive cancelled organizing work");

        // A simulated catalog-commit failure leaves a prepared move with a real newly created folder.
        var recovering=await Proposed(create:"Recovery training");var recoveryPlan=await library.PreviewOrganizationFilingAsync(recovering.Job.Id,Choice(name:"Recovery guide.txt",create:"Recovery training"),actor);
        await using(var db=await factory.CreateDbContextAsync())
        {
            var options=new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(new FailCompletion()).Options;
            var failing=new LibraryService(new Factory(options),new DocumentTextReader(),clock);var failed=false;
            try{await failing.ApplyOrganizationFilingAsync(recoveryPlan.Id,actor);}catch(InvalidOperationException ex)when(ex.Message=="Synthetic organize commit failure"){failed=true;}
            check(failed&&(await library.EntryAsync(recovering.Entry.Id))!.ParentId==inbox&&File.Exists(Path.Combine(scratch,"Cedar Churhc/Safety Minstry/Recovery training/Recovery guide.txt")),"an interrupted new-folder filing preserves moved bytes while its catalog transaction rolls back");
        }
        await library.ScanAsync();
        check((await library.EntryAsync(recovering.Entry.Id))!.ParentId==recoveryPlan.TargetFolderId&&await library.ApplyOrganizationFilingAsync(recoveryPlan.Id,actor)==(await library.OverviewAsync()).Activity.Single(x=>x.OrganizationFilingId==recoveryPlan.Id).Id,"restart recovery completes a verified organizing move and preserves its idempotent approval receipt");
        check((await library.FilingExamplesAsync()).Count(x=>x.Id==recoveryPlan.Id)==1,"recovery commits exactly one remembered example with the completed move");
        var interrupted=await Proposed(create:"Preserved empty folder");var cancelledPlan=await library.PreviewOrganizationFilingAsync(interrupted.Job.Id,Choice(name:"Cancelled guide.txt",create:"Preserved empty folder"),actor);
        await using(var db=await factory.CreateDbContextAsync())
        {
            var options=new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(new FailAfterFolder(cancelledPlan.TargetFolderId)).Options;
            var failing=new LibraryService(new Factory(options),new DocumentTextReader(),clock);
            try{await failing.ApplyOrganizationFilingAsync(cancelledPlan.Id,actor);}catch(InvalidOperationException ex)when(ex.Message=="Synthetic interruption before file move"){}
        }
        await library.ScanAsync();
        check((await Job(interrupted.Job.Id)).State=="Rejected"&&File.Exists(Path.Combine(scratch,"Incoming",interrupted.Entry.Name))&&Directory.Exists(Path.Combine(scratch,"Cedar Churhc/Safety Minstry/Preserved empty folder"))&&!(await library.FilingExamplesAsync()).Any(x=>x.Id==cancelledPlan.Id),"source-only interrupted organizing cancels its approval, preserves the source and empty folder, and remembers nothing");
        await refuses(()=>library.ApplyOrganizationFilingAsync(cancelledPlan.Id,actor),"a recovery-cancelled organizing approval cannot be retried");
        settings=(await library.IdentificationOverviewAsync()).Settings;settings.AutoIdentify=true;await library.SaveOllamaSettingsAsync(settings,settings.Revision,actor);
        var automatic=await New();await library.QueueAutomaticIdentificationAsync(0);
        await using(var db=await factory.CreateDbContextAsync())check(await db.IdentificationJobs.AnyAsync(x=>x.EntryId==automatic.Id&&x.Organizing&&x.CreatedBy=="automatic"),"automatic reading queues complete organizing proposals rather than requiring a fact profile or rule");
        await library.SetIntakeAsync(inbox,false);
        Directory.Delete(foreignPath,true);
    }
    private sealed class Clock:TimeProvider {private DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;public void Advance(int seconds)=>now=now.AddSeconds(seconds);}
    private sealed class Model(Func<IdentificationRequest,CancellationToken,Task<IdentificationResult>> identify):IOllamaClient
    {public Task<List<LocalModel>> ModelsAsync(string endpoint,CancellationToken cancellationToken=default)=>Task.FromResult(new List<LocalModel>());public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request,CancellationToken cancellationToken=default)=>identify(request,cancellationToken);}
    private sealed class Factory(DbContextOptions<CatalogDb> options):IDbContextFactory<CatalogDb>{public CatalogDb CreateDbContext()=>new(options);}
    private sealed class ChangeBeforeSnapshot(string source):DbCommandInterceptor
    {
        public bool Changed {get;private set;}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            // The path-only inventory runs after the approved fingerprint check and immediately before the journal snapshot.
            if(!Changed&&command.CommandText.Contains("THEN e.\"Instructions\""))
            {var modified=File.GetLastWriteTimeUtc(source);var size=(int)new FileInfo(source).Length;File.WriteAllText(source,new string('Z',size));File.SetLastWriteTimeUtc(source,modified);Changed=true;}
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailCompletion:SaveChangesInterceptor
    {public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {if(eventData.Context!.ChangeTracker.Entries<FileOperation>().Any(x=>x.Entity.Kind=="Organize"&&x.Entity.State=="Completed"))throw new InvalidOperationException("Synthetic organize commit failure");return ValueTask.FromResult(result);}}
    private sealed class FailAfterFolder(Guid folder):SaveChangesInterceptor
    {public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,int result,CancellationToken cancellationToken=default)
        {if(eventData.Context!.ChangeTracker.Entries<CatalogEntry>().Any(x=>x.Entity.Id==folder))throw new InvalidOperationException("Synthetic interruption before file move");return ValueTask.FromResult(result);}}
}
