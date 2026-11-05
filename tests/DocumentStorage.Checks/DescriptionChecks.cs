using System.Text;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class DescriptionChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory,string scratch,Action<bool,string> check,Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);var clock=new Clock();var library=new LibraryService(factory,new DocumentTextReader(),clock);
        var root=await library.AddRootAsync("Synthetic description archive",scratch,false);var top=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.ParentId==null);
        var inbox=await library.CreateFolderAsync(top.Id,"Incoming");var target=await library.CreateFolderAsync(top.Id,"Safety Ministry");await library.SetIntakeAsync(inbox,true);
        await library.UploadAsync(inbox,"guide.txt",new MemoryStream(Encoding.UTF8.GetBytes("Synthetic North campus safety ministry. ORIGINAL-OCR-ONLY-SENTINEL.")));
        clock.Advance();await library.ScanAsync();var entry=(await library.BrowseAsync(inbox)).Items.Single();
        var settings=(await library.IdentificationOverviewAsync()).Settings;settings.Enabled=true;settings.Model="synthetic:latest";settings.AutoIdentify=false;await library.SaveOllamaSettingsAsync(settings,settings.Revision,"owner");
        IdentificationResult Result()=>new("ministry_guide",new(){["campus"]="North"},new(){["campus"]="North campus"},"Synthetic volunteer procedures.",false,new(target,"","North guide.txt","Matches the ministry."),new("Volunteer orientation",["Training","Policy","training"],["Synthetic Church"]));
        await library.QueueOrganizationAsync(entry.Id,"owner");await library.ProcessNextIdentificationAsync(new Model((_,_)=>Task.FromResult(Result())));
        var saved=(await library.DescriptionAsync(entry.Id))!;
        check(saved.Title=="Volunteer orientation"&&saved.Tags.SequenceEqual(new[]{"training","policy"})&&saved.Organizations.Contains("Synthetic Church"),"a novel document receives multiple reusable tags and a durable independent description");
        check((await library.KnownTagsAsync()).Contains("training")&&(await library.BrowseAsync(null,"orientation policy")).Items.Any(x=>x.Id==entry.Id),"tags, title and summary participate in library search and reusable vocabulary");
        await library.SaveDescriptionAsync(entry.Id,saved.Revision,"Volunteer handbook","Reference for volunteers.",["Training","Operations"],["Synthetic Church"],"owner");
        await refuses(()=>library.SaveDescriptionAsync(entry.Id,saved.Revision,"Stale title","",[],[],"owner"),"stale description edits cannot replace a newer correction");
        await refuses(()=>library.SaveDescriptionAsync(entry.Id,saved.Revision+1,"Bad tags","",[new string('x',65)],[],"owner"),"description values are bounded before persistence");
        await library.CreateFolderAsync(top.Id,"Another destination");clock.Advance();
        var placement=await library.QueuePlacementAsync(entry.Id,"owner");
        await library.ProcessNextIdentificationAsync(new Model((input,_)=>
        {
            check(input.InputText.Contains("SAVED DOCUMENT DESCRIPTION")&&input.InputText.Contains("Volunteer handbook")&&!input.InputText.Contains("ORIGINAL-OCR-ONLY-SENTINEL"),"placement sends saved understanding without the original OCR");
            check(input.ContextJson.Contains("operations"),"placement context includes reusable saved tags");
            return Task.FromResult(Result());
        }));
        var after=(await library.DescriptionAsync(entry.Id))!;
        check(after.EditedByUser&&after.Title=="Volunteer handbook"&&after.Revision==saved.Revision+1,"placement preserves corrected understanding rather than overwriting it with another model response");
        var plan=await library.PreviewOrganizationFilingAsync(placement,new(target,"","Approved handbook.txt","ministry_guide","",false),"owner");
        await library.SaveDescriptionAsync(entry.Id,after.Revision,"Revised handbook",after.Summary,after.Tags,after.Organizations,"owner");
        await refuses(()=>library.ApplyOrganizationFilingAsync(plan.Id,"owner"),"description edits invalidate an already previewed placement approval");
        await library.RejectOrganizationAsync(placement,"owner");
        check((await library.DescriptionAsync(entry.Id))!.Title=="Revised handbook","declining a stale placement preserves saved document understanding");
        clock.Advance();placement=await library.QueuePlacementAsync(entry.Id,"owner");
        await library.ProcessNextIdentificationAsync(new Model((_,_)=>Task.FromResult(Result())));
        plan=await library.PreviewOrganizationFilingAsync(placement,new(target,"","Approved handbook.txt","ministry_guide","",false),"owner");await library.ApplyOrganizationFilingAsync(plan.Id,"owner");
        check((await library.DescriptionAsync(entry.Id))!.Title=="Revised handbook"&&(await library.BrowseAsync(null,"operations handbook")).Items.Any(x=>x.Id==entry.Id),"saved descriptions and tags follow document identity through approved filing");
        await library.SetIntakeAsync(inbox,false);settings=(await library.IdentificationOverviewAsync()).Settings;settings.Enabled=false;await library.SaveOllamaSettingsAsync(settings,settings.Revision,"owner");
    }
    private sealed class Clock:TimeProvider { private DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;public void Advance()=>now=now.AddSeconds(6); }
    private sealed class Model(Func<IdentificationRequest,CancellationToken,Task<IdentificationResult>> read):IOllamaClient
    {public Task<List<LocalModel>> ModelsAsync(string endpoint,CancellationToken token=default)=>Task.FromResult(new List<LocalModel>());public Task<IdentificationResult> IdentifyAsync(IdentificationRequest input,CancellationToken token=default)=>read(input,token);}
}
