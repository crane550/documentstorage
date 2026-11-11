using System.Text;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class NamingModeChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory,string scratch,Action<bool,string> check,Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);
        var clock=new Clock();var library=new LibraryService(factory,new DocumentTextReader(),clock);const string actor="naming-mode-owner";
        var root=await library.AddRootAsync("Synthetic naming defaults",scratch,false);
        var top=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.ParentId==null);
        var inbox=await library.CreateFolderAsync(top.Id,"Inbox");await library.SetIntakeAsync(inbox,true);
        var kind=await library.SaveKindAsync(new(){Name="Monthly account report",FolderPattern="Reports/{from}",NamePattern="{date} report"},actor);
        await library.SaveSenderAsync(Guid.Empty,"Naming Test Bank",[],actor);
        async Task<DocumentKind> Kind()=> (await library.SortVocabularyAsync()).Kinds.Single(x=>x.Id==kind.Id);
        var sequence=0;
        async Task<SortCard> Arrive(string name,string reportKind="Monthly account report",string sender="Naming Test Bank")
        {
            sequence++;var body=$"{reportKind}\n{sender}\nDate: 2026-{sequence:00}-28\nSynthetic statement sequence {sequence}. Nothing here is private.";
            await library.UploadAsync(inbox,name,new MemoryStream(Encoding.UTF8.GetBytes(body)));
            clock.Advance(6);await library.ScanAsync();clock.Advance(6);await library.ScanAsync();
            return (await library.SortCardAsync((await library.BrowseAsync(inbox)).Items.Single(x=>x.Name==name).Id))!;
        }
        var first=await Arrive("Monthly account report January.txt");
        check(first.Choice.Kind==kind.Name&&first.Choice.KeepName&&(await Kind()).KeepOriginalName==null,"a kind without a saved preference retains descriptive-filename behavior");
        var built=first.Choice with{KeepName=false,RememberNameMode=true};await library.PlanSortAsync(first.Document.Id,built);
        check((await Kind()).KeepOriginalName==null,"previewing a naming choice does not teach it before approval");
        await library.FileSortedAsync(first.Document.Id,built,first.Document.ContentHash,actor);
        check((await Kind()).KeepOriginalName==false&&File.Exists(Path.Combine(scratch,"Reports/Naming Test Bank/2026-01-28 report.txt")),"approving Built saves the kind's preference with the real filing");
        var second=await Arrive("Monthly account report February.txt");
        check(!second.Choice.KeepName&&second.Plan.FileName=="2026-02-28 report.txt","the next descriptive report uses Built instead of Original");
        library=new LibraryService(factory,new DocumentTextReader(),clock);second=(await library.SortCardAsync(second.Document.Id))!;
        check(!second.Choice.KeepName,"the naming preference survives a new service instance");
        await library.FileSortedAsync(second.Document.Id,second.Choice with{KeepName=true,RememberNameMode=true},second.Document.ContentHash,actor);
        check((await Kind()).KeepOriginalName==true,"approving Original replaces the previous Built preference");
        var third=await Arrive("IMG_0003.txt");
        var settings=(await library.IdentificationOverviewAsync()).Settings;settings.Enabled=true;settings.AutoIdentify=false;settings.Model="synthetic-naming";
        await library.SaveOllamaSettingsAsync(settings,settings.Revision,actor);
        await library.QueueSortingAsync(third.Document.Id,actor);
        await library.ProcessNextIdentificationAsync(new Model());
        third=(await library.SortCardAsync(third.Document.Id))!;
        check(third.Sources.GetValueOrDefault("kind")=="reader"&&third.Choice.KeepName&&third.Plan.FileName=="IMG_0003.txt","a saved Original preference also applies after the AI identifies a scanner-named file");
        await library.FileSortedAsync(third.Document.Id,third.Choice,third.Document.ContentHash,actor);
        var fourth=await Arrive("IMG_0004.txt");
        check(fourth.Sources.GetValueOrDefault("kind")=="memory"&&fourth.Choice.KeepName,"a recurring document filled from memory uses the same saved naming preference");
        await library.FileSortedAsync(fourth.Document.Id,fourth.Choice with{KeepName=false,NameOverride="One-off custom name.txt",RememberNameMode=true},fourth.Document.ContentHash,actor);
        check((await Kind()).KeepOriginalName==true,"a custom filename cannot replace the kind's naming default");
        var fifth=await Arrive("IMG_0005.txt");
        await refuses(()=>library.FileSortedAsync(fifth.Document.Id,fifth.Choice with{KeepName=false,RememberNameMode=true},new string('0',64),actor),"a stale filing is refused before learning a naming preference");
        check((await Kind()).KeepOriginalName==true,"a rejected filing leaves the remembered naming mode unchanged");
        var filing=await library.FileSortedAsync(fifth.Document.Id,fifth.Choice with{KeepName=false,RememberNameMode=true},fifth.Document.ContentHash,actor);
        var sixth=await Arrive("Monthly account report June.txt");
        check(!sixth.Choice.KeepName&&sixth.Sources.GetValueOrDefault("kind")=="memory"&&sixth.JobId==null,"the next report uses the remembered Built default from memory without an AI job");
        var preferenceBeforeEdit=(await Kind()).KeepOriginalName;var editedKind=await Kind();editedKind.Description="Synthetic updated description";await library.SaveKindAsync(editedKind,actor);
        check((await Kind()).KeepOriginalName==preferenceBeforeEdit,"editing kind patterns and fields preserves its remembered naming choice");
        var other=await library.SaveKindAsync(new(){Name="Separate naming kind",FolderPattern="Separate",NamePattern="{date} separate"},actor);
        var separate=await Arrive("Separate naming kind July.txt","Separate naming kind","Separate Test Sender");
        check(separate.Choice.Kind==other.Name&&separate.Choice.KeepName,"one kind's Built preference does not affect another kind");
        await library.FileSortedAsync(separate.Document.Id,separate.Choice with{KeepName=false},separate.Document.ContentHash,actor);
        check((await library.SortVocabularyAsync()).Kinds.Single(x=>x.Id==other.Id).KeepOriginalName==null,"an approval without an explicit mode choice does not silently teach a default");
        var fresh=await Arrive("New category August.txt");
        await library.FileSortedAsync(fresh.Document.Id,fresh.Choice with{Kind="New naming category",KeepName=false,RememberNameMode=true},fresh.Document.ContentHash,actor);
        check((await library.SortVocabularyAsync()).Kinds.Single(x=>x.Name=="New naming category").KeepOriginalName==false,"the first approval can teach a naming preference to a new kind");
        await library.SetIntakeAsync(inbox,false);
    }
    private sealed class Clock:TimeProvider{private DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;public void Advance(int seconds)=>now=now.AddSeconds(seconds);}
    private sealed class Model:IOllamaClient
    {
        public Task<List<LocalModel>> ModelsAsync(string endpoint,CancellationToken cancellationToken=default)=>Task.FromResult(new List<LocalModel>());
        public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request,CancellationToken cancellationToken=default)=>throw new InvalidOperationException();
        public Task<string> AskAsync(AskRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(ScriptedReader.Answer(request,new SortReply("Monthly account report","","Naming Test Bank","2026-03-28","Monthly report",[],[],"high")));
    }
}
