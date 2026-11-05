using System.Text;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class InboxChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch,
        Action<bool,string> check, Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock);
        var root = await library.AddRootAsync("Synthetic inbox workbench", scratch, false);
        var top = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var incoming = await library.CreateFolderAsync(top.Id, "Incoming");
        await library.SetIntakeAsync(incoming, true);
        await library.UploadAsync(incoming, "receipt.txt", new MemoryStream(Encoding.UTF8.GetBytes("Synthetic paid tool receipt.")));
        var entry = (await library.BrowseAsync(incoming)).Items.Single(x => !x.IsFolder);
        clock.Advance(6); await library.ScanAsync();
        var review = await library.SaveReviewAsync(entry.Id, 0, "receipt", new() { ["counterparty"] = "Synthetic Tools" }, false, "inbox-owner");
        var finished = new IdentificationJob { EntryId = entry.Id, State = "Succeeded", Organizing = true };
        var running = new IdentificationJob { EntryId = entry.Id, State = "Running", RunId = Guid.NewGuid(), LeaseUntil = clock.GetUtcNow().AddMinutes(5) };
        await using (var db = await factory.CreateDbContextAsync())
        {
            var cached = await db.Entries.SingleAsync(x => x.Id == entry.Id);
            cached.ExtractedText = ""; cached.TextStatus = "Needs OCR"; cached.PassageRevision = "old-index";
            db.IdentificationJobs.AddRange(finished, running); await db.SaveChangesAsync();
        }
        await library.ScanAsync();
        check((await library.EntryAsync(entry.Id))!.TextStatus == "Needs OCR", "ordinary discovery does not force extraction of unchanged files");
        var result = await library.RescanDocumentAsync(entry.Id, "inbox-owner", "Incoming/receipt.txt");
        check(result.TextStatus == "Indexed" && result.Characters > 0 && (await library.EntryAsync(entry.Id))!.PassageRevision == "",
            "per-document rescan replaces cached failure and invalidates passage index without changing source bytes");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var jobs = await db.IdentificationJobs.Where(x => x.EntryId == entry.Id).ToListAsync();
            check(jobs.All(x => x.State == "Superseded" && x.RunId == null && x.LeaseUntil == null), "rescan supersedes successful and running AI proposals even when PDF bytes are unchanged");
            check((await db.Reviews.SingleAsync(x => x.EntryId == entry.Id)).Revision == review.Revision, "rescan retains the user's saved review draft");
        }
        await refuses(() => library.RescanDocumentAsync(entry.Id, "inbox-owner", "old/receipt.txt"), "stale source path cannot rescan a different location");
        await refuses(() => library.TrashEntryAsync(entry.Id, "old/receipt.txt"), "stale delete confirmation cannot trash a relocated document");

        // Add enough synthetic entries to exercise real queue ordering across a page boundary.
        var extra = Enumerable.Range(0, 105).Select(i => new CatalogEntry { RootId = root, ParentId = incoming, Name = $"page-{i:D3}.txt",
            RelativePath = $"Incoming/page-{i:D3}.txt", ObservedAt = clock.GetUtcNow().AddSeconds(i + 1) }).ToList();
        await using (var db = await factory.CreateDbContextAsync()) { db.Entries.AddRange(extra); await db.SaveChangesAsync(); }
        await library.SaveInboxPositionAsync("inbox-owner", extra[^1].Id, 100);
        var restored = await new LibraryService(factory, new DocumentTextReader(), clock).InboxPositionAsync("inbox-owner");
        check(restored.EntryId == extra[^1].Id && restored.Offset == 100, "inbox position survives a new service instance and includes the second page");
        check((await library.InboxPositionAsync("different-owner")).EntryId == null, "inbox progress is isolated by signed-in actor");
        check((await library.InboxPositionAsync("inbox-owner", entry.Id)).Offset == 0, "an explicit document link overrides saved position");
        await using (var db = await factory.CreateDbContextAsync()) { db.Entries.RemoveRange(extra.Take(10)); await db.SaveChangesAsync(); }
        check((await library.InboxPositionAsync("inbox-owner")).Offset == 0, "saved document follows removals which shift it onto the previous page");
        await using (var db = await factory.CreateDbContextAsync()) { db.Entries.RemoveRange(extra.Skip(10)); await db.SaveChangesAsync(); }

        var text = (await library.EntryAsync(entry.Id))!.ExtractedText;
        var marker = Path.Combine(scratch, StoragePaths.Marker);
        File.Move(marker, marker + ".offline");
        await refuses(() => library.RescanDocumentAsync(entry.Id, "inbox-owner", "Incoming/receipt.txt"), "offline storage refuses forced extraction");
        check((await library.EntryAsync(entry.Id))!.ExtractedText == text, "offline rescan preserves the existing searchable text");
        File.Move(marker + ".offline", marker);
        await library.TrashEntryAsync(entry.Id, "Incoming/receipt.txt");
        check(!(await library.IntakeAsync()).Items.Any(x => x.Entry.Id == entry.Id) && (await library.EntryAsync(entry.Id))!.TrashBatchId != null,
            "inbox delete uses recoverable trash and removes the document from intake");
        await refuses(() => library.RescanDocumentAsync(entry.Id, "inbox-owner", "Incoming/receipt.txt"), "trashed documents cannot be rescanned as inbox entries");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
