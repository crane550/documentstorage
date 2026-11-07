using System.Text;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var connection = Environment.GetEnvironmentVariable("DOCUMENTSTORAGE_CHECKS_DB") ?? "Host=127.0.0.1;Port=55432;Database=documentstorage_checks;Username=postgres";
var settings = new NpgsqlConnectionStringBuilder(connection);
if (!(settings.Host is "localhost" or "127.0.0.1" or "::1") || settings.Database?.EndsWith("_checks", StringComparison.Ordinal) != true)
    throw new InvalidOperationException("Checks recreate the database. Only loopback databases ending _checks are allowed.");
var options = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(connection).Options;
IDbContextFactory<CatalogDb> factory = new CheckFactory(options);
var library = new LibraryService(factory, new DocumentTextReader());
var scratch = Path.Combine(Path.GetTempPath(), "documentstorage-checks-" + Guid.NewGuid());
var rootPath = Path.Combine(scratch, "archive");
var otherPath = Path.Combine(scratch, "network");
Directory.CreateDirectory(rootPath);
Directory.CreateDirectory(otherPath);
var passed = 0;
void Check(bool valid, string description)
{
    if (!valid) throw new Exception("FAIL " + description);
    Console.WriteLine("PASS " + description); passed++;
}
async Task Refuses(Func<Task> action, string description)
{
    try { await action(); } catch (StorageException) { Check(true, description); return; }
    throw new Exception("FAIL did not refuse: " + description);
}
try
{
    await MigrationChecks.RunAsync(factory, Check);
    Check(StoragePaths.ValidateName("Bank statements") == "Bank statements", "portable names accepted");
    foreach (var name in new[] { "../outside", "a/b", "a\\b", "CON.pdf", ".documentstorage-root", "trailing.", "" })
        await Refuses(() => Task.Run(() => StoragePaths.ValidateName(name)), "unsafe name refused: " + name);
    await Refuses(() => Task.Run(() => StoragePaths.Resolve(rootPath, "../outside")), "path traversal refused");
    await Refuses(() => library.AddRootAsync("Whole drive", Path.GetPathRoot(rootPath)!, false), "whole filesystem roots are not registered as document storage");
    await File.WriteAllTextAsync(Path.Combine(rootPath, "readme.txt"), "Synthetic archive: Cedar Bank statement closing October 31, 2026.");
    await File.WriteAllBytesAsync(Path.Combine(rootPath, "statement.pdf"), MakePdf("Synthetic Cedar statement October 2026"));
    var rootId = await library.AddRootAsync("Personal archive", rootPath, false);
    var overview = await library.OverviewAsync();
    var rootFolder = overview.Folders.Single(x => x.RootId == rootId && x.ParentId == null);
    Check(overview.Roots.Single().IsOnline && overview.Documents == 2, "root registration indexes existing files in place");
    Check(File.Exists(Path.Combine(rootPath, "readme.txt")), "registration preserves original paths");
    var search = await library.BrowseAsync(null, "Cedar closing");
    Check(search.Total == 1 && search.Items.Single().Name == "readme.txt", "search matches extracted text with all query terms");
    var pdfSearch = await library.BrowseAsync(null, "Synthetic Cedar statement October");
    Check(pdfSearch.Items.Any(x => x.Name == "statement.pdf" && x.TextStatus == "Indexed"), "PDF OCR text layer extracted and searched");
    Check((await library.BrowseAsync(null, "%")).Total == 0, "search escapes wildcard characters");
    Directory.CreateDirectory(Path.Combine(rootPath, "nested"));
    await Refuses(() => library.AddRootAsync("Overlap", Path.Combine(rootPath, "nested"), false), "overlapping storage roots refused");
    var bankId = await library.CreateFolderAsync(rootFolder.Id, "Banking");
    var yearId = await library.CreateFolderAsync(bankId, "2026");
    await library.SaveInstructionsAsync(bankId, "Keep monthly statements. Use the closing date.");
    await library.UploadAsync(yearId, "october.txt", new MemoryStream(Encoding.UTF8.GetBytes("Synthetic statement balance 42.50")));
    var doc = (await library.BrowseAsync(yearId)).Items.Single();
    await Refuses(() => library.UploadAsync(yearId, "october.txt", new MemoryStream([1, 2, 3])), "upload collision never overwrites");
    Check((await File.ReadAllTextAsync(Path.Combine(rootPath, "Banking/2026/october.txt"))).Contains("42.50"), "original bytes preserved after collision");
    await library.MoveAsync(bankId, rootFolder.Id, "Financial records");
    Check(Directory.Exists(Path.Combine(rootPath, "Financial records/2026")), "folder rename moves real directories");
    Check((await library.EntryAsync(doc.Id))!.RelativePath == "Financial records/2026/october.txt", "descendant paths update without changing document IDs");
    Check((await library.EntryAsync(bankId))!.Instructions.StartsWith("Keep monthly"), "folder guidance survives rename");
    await Refuses(() => library.MoveAsync(bankId, yearId, "Recursive"), "moving a folder inside itself refused");
    await library.MoveAsync(doc.Id, bankId, "2026-10-31 - Cedar.txt");
    Check(File.Exists(Path.Combine(rootPath, "Financial records/2026-10-31 - Cedar.txt")), "document rename and move work together");
    await library.TrashEntryAsync(bankId);
    Check(!Directory.Exists(Path.Combine(rootPath, "Financial records")) && (await library.TrashAsync()).Count == 1, "folder trash is recoverable and groups descendants");
    Check((await library.BrowseAsync(null, "42.50")).Total == 0, "trash excluded from search");
    await library.RestoreAsync(bankId);
    Check(File.Exists(Path.Combine(rootPath, "Financial records/2026-10-31 - Cedar.txt")) && (await library.EntryAsync(doc.Id))!.TrashBatchId == null, "folder restore restores descendants and identity");
    await library.TrashEntryAsync(doc.Id);
    await File.WriteAllTextAsync(Path.Combine(rootPath, "Financial records/2026-10-31 - Cedar.txt"), "new original-path occupant");
    await Refuses(() => library.RestoreAsync(doc.Id), "restore collision preserves both items");
    File.Delete(Path.Combine(rootPath, "Financial records/2026-10-31 - Cedar.txt"));
    await library.RestoreAsync(doc.Id);
    var networkId = await library.AddRootAsync("NAS", otherPath, true);
    var networkFolder = (await library.OverviewAsync()).Folders.Single(x => x.RootId == networkId && x.ParentId == null);
    await Refuses(() => library.MoveAsync(doc.Id, networkFolder.Id, doc.Name), "cross-root move deferred without touching source");
    var before = (await library.OverviewAsync()).Documents;
    Directory.Move(rootPath, rootPath + "-offline");
    await library.ScanAsync();
    overview = await library.OverviewAsync();
    Check(!overview.Roots.Single(x => x.Id == rootId).IsOnline && overview.Documents == before, "offline storage retains index without marking documents deleted");
    Check((await library.BrowseAsync(null, "42.50")).Total == 1, "offline document text remains searchable");
    await Refuses(() => library.TrashEntryAsync(doc.Id), "offline write refused");
    Directory.Move(rootPath + "-offline", rootPath);
    await library.ScanAsync();
    Check((await library.OverviewAsync()).Roots.Single(x => x.Id == rootId).IsOnline, "reconnected storage returns online");
    var outside = Path.Combine(scratch, "outside"); Directory.CreateDirectory(outside);
    Directory.CreateSymbolicLink(Path.Combine(rootPath, "escape"), outside);
    await library.ScanAsync();
    Check(!(await library.OverviewAsync()).Folders.Any(x => x.Name == "escape"), "scanner excludes symbolic-link directories");
    await Refuses(() => Task.Run(() => StoragePaths.Resolve(rootPath, "escape/secret.txt")), "symlink traversal refused");
    Directory.Delete(Path.Combine(rootPath, "escape"));
    // Simulate a crash after the filesystem rename and before the catalog commit.
    doc = (await library.EntryAsync(doc.Id))!;
    var interrupted = new FileOperation { RootId = rootId, EntryId = doc.Id, Kind = "Move", FromPath = doc.RelativePath, ToPath = "recovered.txt", TargetParentId = rootFolder.Id };
    interrupted.SourceSnapshotJson = System.Text.Json.JsonSerializer.Serialize(await StorageSnapshotter.CaptureAsync(rootPath, doc.RelativePath));
    await using (var db = await factory.CreateDbContextAsync()) { db.Operations.Add(interrupted); await db.SaveChangesAsync(); }
    File.Move(StoragePaths.Resolve(rootPath, doc.RelativePath), Path.Combine(rootPath, "recovered.txt"));
    await library.ScanAsync();
    Check((await library.EntryAsync(doc.Id))!.RelativePath == "recovered.txt", "restart recovery completes an interrupted disk move with original ID");
    Check((await library.OverviewAsync()).Activity.Single(x => x.Id == interrupted.Id).State == "Completed", "recovered operation recorded as completed");
    File.Delete(Path.Combine(rootPath, "readme.txt"));
    await library.ScanAsync();
    Check((await library.BrowseAsync(null, "closing")).Total == 0, "successful scan hides files deleted outside the app");
    var rule = new FilingRule(Guid.NewGuid(), "bank_statement", yearId, new() { ["institution"] = "Cedar" }, "{statement_end:yyyy-MM-dd} - {institution}.pdf", ["statement_end"]);
    var facts = new DocumentFacts("bank_statement", false, new() { ["institution"] = "Cedar", ["statement_end"] = "2026-10-31" });
    Check(FilingRules.Preview(facts, [rule]).Filename == "2026-10-31 - Cedar.pdf", "deterministic rule renders structured facts without AI");
    Check(!FilingRules.Preview(facts with { NeedsReview = true }, [rule]).Ready, "uncertain identification requires review");
    Check(!FilingRules.Preview(facts, [rule, rule with { Id = Guid.NewGuid() }]).Ready, "conflicting rules require review");
    Check(!FilingRules.Preview(facts with { Values = new() { ["institution"] = "Cedar" } }, [rule]).Ready, "missing required facts require review");
    Check(!FilingRules.Preview(facts with { Values = new() { ["institution"] = "Cedar", ["statement_end"] = "10/31/26" } }, [rule]).Ready, "ambiguous dates refused");
    Check(!FilingRules.Preview(facts, [rule with { FilenameTemplate = "../outside.pdf" }]).Ready, "rule templates cannot escape destination folder");
    Check((await library.OverviewAsync()).Activity.Any(x => x.Kind == "Trash") && (await library.TrashAsync()).Count == 0, "activity persists after restored trash is empty");
    var beforeMove = new FileOperation { RootId = rootId, EntryId = doc.Id, Kind = "Move", FromPath = "recovered.txt", ToPath = "never-moved.txt", TargetParentId = rootFolder.Id };
    await using (var db = await factory.CreateDbContextAsync()) { db.Operations.Add(beforeMove); await db.SaveChangesAsync(); }
    await library.ScanAsync();
    Check((await library.OverviewAsync()).Activity.Single(x => x.Id == beforeMove.Id).State == "Cancelled", "recovery cancels an interrupted operation that never moved bytes");
    var ambiguous = new FileOperation { RootId = rootId, EntryId = doc.Id, Kind = "Move", FromPath = "recovered.txt", ToPath = "ambiguous.txt", TargetParentId = rootFolder.Id };
    await File.WriteAllTextAsync(Path.Combine(rootPath, "ambiguous.txt"), "A different document");
    await using (var db = await factory.CreateDbContextAsync()) { db.Operations.Add(ambiguous); await db.SaveChangesAsync(); }
    await library.ScanAsync();
    Check((await library.OverviewAsync()).Activity.Single(x => x.Id == ambiguous.Id).State == "Needs review", "ambiguous interrupted moves require review");
    await Refuses(() => library.MoveAsync(doc.Id, rootFolder.Id, "do-not-run.txt"), "ambiguous recovery pauses further writes");
    Check(File.Exists(Path.Combine(rootPath, "recovered.txt")) && File.Exists(Path.Combine(rootPath, "ambiguous.txt")), "ambiguous recovery preserves both disk files");
    await IntakeChecks.RunAsync(factory, Path.Combine(scratch, "filing"), Check, Refuses);
    await InboxChecks.RunAsync(factory, Path.Combine(scratch, "inbox"), Check, Refuses);
    await IdentificationChecks.RunAsync(factory, Path.Combine(scratch, "identification"), Check, Refuses);
    await DescriptionChecks.RunAsync(factory, Path.Combine(scratch, "descriptions"), Check, Refuses);
    await AssistantChecks.RunAsync(factory, Path.Combine(scratch, "assistants"), Check, Refuses);
    await RecoveryChecks.RunAsync(factory, Path.Combine(scratch, "recovery"), Check, Refuses);
    await OrganizingChecks.RunAsync(factory, Path.Combine(scratch, "organizing"), Check, Refuses);
    await DeskChecks.RunAsync(factory, Path.Combine(scratch, "conversations"), Check, Refuses);
    await SortingChecks.RunAsync(factory, Path.Combine(scratch, "sorting"), Check, Refuses);
    await RegularsChecks.RunAsync(factory, Path.Combine(scratch, "regulars"), Check, Refuses);
    await KindFieldChecks.RunAsync(factory, Path.Combine(scratch, "fields"), Check, Refuses);
    await FieldGuideChecks.RunAsync(factory, Path.Combine(scratch, "guides"), Check, Refuses);
    await DuplicateChecks.RunAsync(factory, Path.Combine(scratch, "duplicates"), Check, Refuses);
    Console.WriteLine($"\nAll {passed} checks passed against PostgreSQL and real temporary files.");
}
finally
{
    Directory.Delete(scratch, recursive: true);
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureDeletedAsync();
}

static byte[] MakePdf(string text)
{
    var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount($"BT /F1 12 Tf 40 700 Td ({text}) Tj ET")} >>\nstream\nBT /F1 12 Tf 40 700 Td ({text}) Tj ET\nendstream" };
    var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
    for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
    var xref = pdf.Length; pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (var offset in offsets.Skip(1)) pdf.Append($"{offset:D10} 00000 n \n");
    pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.ASCII.GetBytes(pdf.ToString());
}

sealed class CheckFactory(DbContextOptions<CatalogDb> options) : IDbContextFactory<CatalogDb>
{
    public CatalogDb CreateDbContext() => new(options);
}
