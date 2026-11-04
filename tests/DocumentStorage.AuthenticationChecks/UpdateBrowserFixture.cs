using System.Text.Json;
using DocumentStorage.Data;
using DocumentStorage.Web.Updates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentStorage.AuthenticationChecks;

// Deliberately separate executable test mode. Production has no mock updater configuration or endpoint.
internal static class UpdateBrowserFixture
{
    public static async Task RunAsync()
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while(source!=null&&!File.Exists(Path.Combine(source.FullName,"DocumentStorage.sln")))source=source.Parent;
        var root=source!.FullName;
        var credentials=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,".local/browser-credentials.json"))).RootElement;
        var scratch=Path.Combine(root,".local","updater-browser-"+Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(scratch,"requests"));
        var host=new SyntheticUpdateHost(scratch) {Installed=true};
        const string connection="Host=127.0.0.1;Port=55432;Database=documentstorage_updater_browser_checks;Username=postgres";
        foreach(var (key,value) in new Dictionary<string,string?> { ["Hosting__PublicOrigin"]="",["Hosting__DataProtectionDirectory"]=Path.Combine(scratch,"keys"),["BootstrapAdmin__Username"]=credentials.GetProperty("username").GetString(),["BootstrapAdmin__Password"]=credentials.GetProperty("password").GetString(),["ConnectionStrings__Catalog"]=connection,["DOCUMENTSTORAGE_INSTANCE"]=null,["DOCUMENTSTORAGE_DB_PASSWORD"]=null,["Urls"]="http://127.0.0.1:5248"})Environment.SetEnvironmentVariable(key,value);
        var options=new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(connection).Options;
        await using(var db=new CatalogDb(options))await db.Database.EnsureDeletedAsync();
        await using var app=new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder=>
        {builder.UseEnvironment("Development");builder.UseContentRoot(Path.Combine(root,"src/DocumentStorage.Web"));builder.ConfigureServices(services=>services.AddSingleton<IUpdateHost>(host));});
        app.UseKestrel(5248);
        using var client=app.CreateClient(new WebApplicationFactoryClientOptions {BaseAddress=new Uri("http://127.0.0.1:5248")});
        await client.GetAsync("/health");
        await File.WriteAllTextAsync(Path.Combine(root,".local/updater-browser.pid"),Environment.ProcessId.ToString());
        Console.WriteLine("Synthetic updater browser fixture ready on loopback 5248; no Windows task or deployment is invoked.");
        await Task.Delay(TimeSpan.FromMinutes(20));
    }
}
