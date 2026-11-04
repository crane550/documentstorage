// Standalone, destructive synthetic fixture for the browser recovery workflow. Never runs in the app.
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var connection = Environment.GetEnvironmentVariable("DOCUMENTSTORAGE_RECOVERY_FIXTURE_DB") ?? "Host=127.0.0.1;Port=55432;Database=documentstorage_browser_recovery_checks;Username=postgres";
var settings = new NpgsqlConnectionStringBuilder(connection);
if (!(settings.Host is "localhost" or "127.0.0.1" or "::1") || settings.Database?.EndsWith("_recovery_checks",StringComparison.Ordinal)!=true || args is not ["--reset"])
    throw new InvalidOperationException("Pass --reset for a disposable loopback database ending _recovery_checks only.");
var options = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(connection).Options;
var factory = new FixtureFactory(options);
await using(var db = factory.CreateDbContext()) { await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync(); }
var directory = Path.GetFullPath(Path.Combine(".local","recovery-fixture-"+Guid.NewGuid())); Directory.CreateDirectory(directory);
Directory.CreateDirectory(Path.Combine(directory,"Original/Empty")); await File.WriteAllTextAsync(Path.Combine(directory,"Original/scan.txt"),"Synthetic original recovery document.");
await File.WriteAllTextAsync(Path.Combine(directory,"Changed.txt"),"Synthetic source survives.");
await File.WriteAllTextAsync(Path.Combine(directory,"Old journal.txt"),"Synthetic legacy recovery document.");
var library = new LibraryService(factory,new DocumentTextReader()); var root = await library.AddRootAsync("Recovery browser archive",directory,false);
var parent = (await library.OverviewAsync()).Folders.Single(x=>x.RootId==root && x.ParentId==null);
var entries = (await library.BrowseAsync(parent.Id)).Items;
async Task Prepare(string from,string to,bool legacy=false)
{
    var entry=entries.Single(x=>x.Name==from);
    if(entry.IsFolder) await library.SaveInstructionsAsync(entry.Id,"Synthetic recovery guidance survives.");
    var op=new FileOperation { RootId=root, EntryId=entry.Id,Kind="Move",FromPath=from,ToPath=to,TargetParentId=parent.Id,State="Needs review",Error="Synthetic interrupted operation for browser verification.",
        SourceSnapshotJson=legacy?"":JsonSerializer.Serialize(await StorageSnapshotter.CaptureAsync(directory,from)) };
    await using var db=factory.CreateDbContext();db.Operations.Add(op);await db.SaveChangesAsync();
}
await Prepare("Original","Recovered");
Directory.CreateDirectory(Path.Combine(directory,"Recovered/Empty"));File.Copy(Path.Combine(directory,"Original/scan.txt"),Path.Combine(directory,"Recovered/scan.txt"));
// Save both remaining operations directly; the storage is now deliberately write-blocked.
await Prepare("Changed.txt","Different.txt");await File.WriteAllTextAsync(Path.Combine(directory,"Different.txt"),"Synthetic different contents.");
await Prepare("Old journal.txt","Legacy destination.txt",true); File.Move(Path.Combine(directory,"Old journal.txt"),Path.Combine(directory,"Legacy destination.txt"));
await File.WriteAllTextAsync(Path.Combine(".local","recovery-fixture.json"),JsonSerializer.Serialize(new { directory, root_id=root, folder_id=entries.Single(x=>x.Name=="Original").Id }));
Console.WriteLine("Synthetic recovery fixture ready. Local metadata is in ignored .local/recovery-fixture.json.");
sealed class FixtureFactory(DbContextOptions<CatalogDb> options):IDbContextFactory<CatalogDb>{public CatalogDb CreateDbContext()=>new(options);}
