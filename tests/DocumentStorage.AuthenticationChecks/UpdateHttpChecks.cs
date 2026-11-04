using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DocumentStorage.Data;
using DocumentStorage.Web;
using DocumentStorage.Web.Updates;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentStorage.AuthenticationChecks;

internal sealed class SyntheticUpdateHost(string directory) : IUpdateHost
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Directory => directory;
    public bool Installed { get; set; }
    public bool Hold { get; set; }
    public bool FailStart { get; set; }
    public int Starts { get; private set; }
    public string Latest { get; set; } = new('b', 40);
    public UpdateRequest? LastRequest { get; private set; }
    public Task<bool> InstalledAsync() => Task.FromResult(Installed);
    public async Task StartAsync()
    {
        Starts++;
        if(FailStart) throw new UpdateException("Synthetic Windows task startup failure.");
        var file = Path.Combine(directory,"requests","pending.json");
        var request = JsonSerializer.Deserialize<UpdateRequest>(await File.ReadAllTextAsync(file),Json)!;
        LastRequest=request;
        var status = new UpdateStatus(request.Id,request.Mode,Hold?"Running":"Succeeded",Hold?"Building synthetic release":"Synthetic task completed",request.Running,Latest,DateTimeOffset.UtcNow,
            [new(Latest,"Synthetic release <script>window.updatePwned=true</script>")],true);
        await WriteAsync("status.json",status);
        if(request.Mode=="check"&&!Hold)await WriteAsync("check.json",status);
        File.Delete(file);
    }
    public Task WriteAsync(string name,UpdateStatus value)=>File.WriteAllTextAsync(Path.Combine(directory,name),JsonSerializer.Serialize(value,Json));
    public void Reset() { foreach(var name in new[]{"status.json","check.json","requests/pending.json"})File.Delete(Path.Combine(directory,name));Hold=false;FailStart=false; }
}
internal static class UpdateHttpChecks
{
    public static async Task RunAsync(IServiceProvider services,HttpClient browser,HttpClient anonymous,SyntheticUpdateHost host,string token,Action<bool,string> check)
    {
        async Task<HttpResponseMessage> Post(string mode,string target="",string id="",bool csrf=true)=>await browser.PostAsync("/updates/"+mode,new FormUrlEncodedContent(new Dictionary<string,string>
            {["target"]=target,["checkId"]=id,["__RequestVerificationToken"]=csrf?token:""}));
        check((await anonymous.GetAsync("/updates/status")).StatusCode==HttpStatusCode.Redirect,"anonymous callers cannot read updater status");
        check((await anonymous.PostAsync("/updates/update",new FormUrlEncodedContent([]))).StatusCode==HttpStatusCode.Redirect,"anonymous callers cannot start deployments");
        var get=await browser.GetAsync("/updates/check");
        check(get.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed && host.Starts==0,"a GET cannot start an update check");
        check((await Post("check",csrf:false)).StatusCode==HttpStatusCode.BadRequest,"updater check requires antiforgery");
        check((await Post("update",csrf:false)).StatusCode==HttpStatusCode.BadRequest,"updater installation requires antiforgery");
        await Post("check");check(host.Starts==0,"an uninstalled updater never invokes a privileged task");
        host.Installed=true;await Post("check");
        var snapshot=(await browser.GetFromJsonAsync<UpdateSnapshot>("/updates/status"))!;
        check(host.Starts==1&&snapshot.Check?.Latest==host.Latest&&snapshot.Check.DatabaseChange,"authenticated check reads a correlated release and migration summary");
        check(host.LastRequest is { Mode:"check",Target:"",CheckId:"" }&&host.LastRequest.Running==AppVersion.Commit&&host.LastRequest.Actor.Length>0,"requests contain fixed modes, running commit and authenticated actor instead of commands or paths");
        var raw=await browser.GetStringAsync("/updates/status");check(!raw.Contains(host.Directory)&&!raw.Contains("actor"),"browser status excludes physical paths and the private request actor");
        var before=host.Starts;await Post("update","main",snapshot.Check!.Id);await Post("update",host.Latest,new string('a',32));
        check(host.Starts==before,"arbitrary revisions and mismatched release checks cannot deploy");
        await host.WriteAsync("check.json",snapshot.Check with {UpdatedAt=DateTimeOffset.UtcNow.AddMinutes(-31)});await Post("update",host.Latest,snapshot.Check.Id);
        check(host.Starts==before,"expired release checks cannot deploy");
        await host.WriteAsync("check.json",snapshot.Check);await Post("update",host.Latest,snapshot.Check.Id);
        check(host.Starts==before+1&&host.LastRequest?.Target==host.Latest&&host.LastRequest.Mode=="update","one approval starts only the checked exact release");
        host.Reset();host.Hold=true;await Post("check");before=host.Starts;await Post("check");
        check(host.Starts==before,"an active task blocks duplicate clicks across HTTP requests");
        host.Reset();host.FailStart=true;await Post("check");before=host.Starts;await Post("check");
        check(host.Starts==before&&File.Exists(Path.Combine(host.Directory,"requests/pending.json")),"uncertain task startup preserves the durable request and blocks duplicate deployment");
        host.Reset();
        await using(var scope=services.CreateAsyncScope())
        {
            var users=scope.ServiceProvider.GetRequiredService<UserManager<LibraryUser>>();var user=(await users.FindByNameAsync("fixture-admin"))!;
            await users.RemoveFromRoleAsync(user,IdentitySetup.Administrator);before=host.Starts;await Post("check");
            check(host.Starts==before,"removing administrator rights blocks a still-signed-in browser before task startup");
            await users.AddToRoleAsync(user,IdentitySetup.Administrator);
        }
        var health=(await browser.GetFromJsonAsync<JsonElement>("/health"));
        check(health.GetProperty("revision").GetString()==AppVersion.Commit&&health.GetProperty("build").GetString()==AppVersion.Build&&AppVersion.Display.EndsWith(AppVersion.Build),"health retains the exact commit while version/build come from immutable assembly metadata");
        host.Installed=false;
    }
}
