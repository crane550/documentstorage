using System.Text;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

// Copies coming in: the same file, the same text in a different file, the same details, the same name.
internal static class DuplicateChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool, string> check, Func<Func<Task>, string, Task> refuses)
    {
        Directory.CreateDirectory(scratch);
        const string body = "SYNTHETIC POWER COMPANY\nService address 1 Example Lane\nBill date April 2, 2026\nAmount due 61.20\nThis synthetic bill exists only for duplicate checks.";
        // Already in the library before Stash knew it, never through an inbox: no fingerprint yet.
        Directory.CreateDirectory(Path.Combine(scratch, "Old papers"));
        await File.WriteAllTextAsync(Path.Combine(scratch, "Old papers", "power april.txt"), body, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(scratch, "Utilities", "Synthetic Power"));
        await File.WriteAllTextAsync(Path.Combine(scratch, "Utilities", "Synthetic Power", "2026-05-02 Synthetic Power bill.txt"), "A hand-filed synthetic page with other words entirely.");
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock); const string actor = "duplicates-owner";
        var root = await library.AddRootAsync("Synthetic duplicates archive", scratch, false);
        var top = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var inbox = await library.CreateFolderAsync(top.Id, "Inbox"); await library.SetIntakeAsync(inbox, true);
        await library.SaveKindAsync(new() { Name = "Synthetic utility bill", Description = "Synthetic power bills.", FolderPattern = "Utilities/{from}", NamePattern = "{date} {from} bill" }, actor);
        await library.SaveSenderAsync(Guid.Empty, "Synthetic Power", [], actor);
        async Task<SortCard> Arrive(string name, byte[] bytes)
        {
            await library.UploadAsync(inbox, name, new MemoryStream(bytes)); clock.Advance(6); await library.ScanAsync(); clock.Advance(6); await library.ScanAsync();
            return (await library.SortCardAsync((await library.BrowseAsync(inbox)).Items.Single(x => x.Name == name).Id))!;
        }
        Guid IdOf(string path) { using var db = factory.CreateDbContext(); return db.Entries.Single(x => x.RootId == root && x.RelativePath == path).Id; }

        var same = await Arrive("scan-1.txt", new UTF8Encoding(false).GetBytes(body));
        var old = IdOf("Old papers/power april.txt");
        check(same.Duplicate && same.Copies.Any(x => x.EntryId == old && x.How == "file" && x.Path == "Old papers/power april.txt" && !x.InInbox),
            "a file already in the library is found, even one that never came through an inbox");
        await using (var db = await factory.CreateDbContextAsync())
            check((await db.Entries.SingleAsync(x => x.Id == old)).ContentHash == same.Document.ContentHash, "the library copy's fingerprint is kept, so the next look is quick");

        // The same text in different bytes (here a byte-order mark; a PDF downloaded twice in practice).
        var marked = await Arrive("scan-2.txt", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(body)).ToArray());
        check(marked.Duplicate && marked.Copies.Any(x => x.EntryId == old && x.How == "text") && marked.Copies.Any(x => x.EntryId == same.Document.Id && x.InInbox),
            "the same text in a different file counts, and a copy still waiting in the inbox says so");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var stored = await db.Entries.SingleAsync(x => x.Id == old);
            var sql = await db.Database.SqlQueryRaw<string>("SELECT encode(sha256(convert_to(\"ExtractedText\", 'UTF8')), 'hex') AS \"Value\" FROM \"Entries\" WHERE \"Id\" = {0}", old).SingleAsync();
            check(stored.TextHash.Length == 64 && stored.TextHash == sql, "the text fingerprint is the one the migration gives documents already read");
        }

        // A copy is never sure, so it never files itself or goes with "Approve all sure".
        var settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = true; settings.Model = "synthetic-sorter";
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        var sure = new SortReply("Synthetic utility bill", "", "Synthetic Power", "2026-04-02", "Power bill", [], [], "high");
        await library.QueueSortingAsync(same.Document.Id, actor); await library.ProcessNextIdentificationAsync(new ScriptedModel(_ => sure));
        check((await library.SortCardAsync(same.Document.Id)) is { Sure: false, Duplicate: true }, "a copy read with full confidence is still not sure");
        var fresh = await Arrive("scan-3.txt", Encoding.UTF8.GetBytes(body.Replace("April 2", "May 2").Replace("61.20", "58.00")));
        await library.QueueSortingAsync(fresh.Document.Id, actor); await library.ProcessNextIdentificationAsync(new ScriptedModel(_ => sure with { Date = "2026-05-02" }));
        var freshCard = (await library.SortCardAsync(fresh.Document.Id))!;
        check(!freshCard.Duplicate && freshCard.Copies.Any(x => x.How == "name" && x.Path == "Utilities/Synthetic Power/2026-05-02 Synthetic Power bill.txt"),
            "a name already taken where it would go is a likely copy, not a certain one");
        check(freshCard.Plan.FileName.EndsWith("(2).txt") && freshCard.Plan.TakenBy == IdOf("Utilities/Synthetic Power/2026-05-02 Synthetic Power bill.txt"), "the plan says which document holds the name");

        // Filed with its details, then something else with the same details arrives.
        await library.FileSortedAsync(same.Document.Id, (await library.SortCardAsync(same.Document.Id))!.Choice, same.Document.ContentHash, actor);
        var filedPath = (await library.EntryAsync(same.Document.Id))!.RelativePath;
        var resent = await Arrive("scan-4.txt", Encoding.UTF8.GetBytes("Synthetic Power. A reminder copy of your bill dated April 2, 2026, printed differently."));
        await library.SaveDocumentDetailsAsync(resent.Document.Id, new("Synthetic utility bill", "Synthetic Power", new DateOnly(2026, 4, 2), "Reminder", [], []), actor);
        var resentCard = (await library.SortCardAsync(resent.Document.Id))!;
        check(!resentCard.Duplicate && resentCard.Copies.Any(x => x.EntryId == same.Document.Id && x.How == "details" && x.Path == filedPath),
            "a filed document with the same kind, sender and date is a likely copy");
        var other = await Arrive("scan-5.txt", Encoding.UTF8.GetBytes("Synthetic Power, a different synthetic notice entirely, for a different service."));
        await library.SaveDocumentDetailsAsync(other.Document.Id, new("Synthetic utility bill", "Synthetic Power", new DateOnly(2026, 6, 2), "Notice", [], []), actor);
        check((await library.SortCardAsync(other.Document.Id))!.Copies.Length == 0, "a different date is a different document");

        // The copy in Trash still says where it went.
        await library.TrashEntryAsync(old);
        check((await library.SortCardAsync(marked.Document.Id))!.Copies.Any(x => x.EntryId == old && x.InTrash), "a copy in Trash is still found, and says it's in Trash");

        settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = false; await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        await library.SetIntakeAsync(inbox, false);
    }

    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds); }
}
