using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

public sealed record DocumentRescan(string TextStatus, int Characters, string Readiness);

public sealed partial class LibraryService
{
    public async Task<InboxPosition> InboxPositionAsync(string actor, Guid? requestedEntry = null)
    {
        Actor(actor);
        await using var db = await factory.CreateDbContextAsync();
        var saved = await db.InboxPositions.AsNoTracking().SingleOrDefaultAsync(x => x.Actor == actor) ?? new() { Actor = actor };
        var wanted = requestedEntry ?? saved.EntryId;
        if (wanted != null)
        {
            // Follow the document when arrivals or removals shift the page boundary.
            var index = 0;
            await foreach (var id in (await IntakeQueryAsync(db)).OrderBy(x => x.ObservedAt).ThenBy(x => x.Name).ThenBy(x => x.Id).Select(x => x.Id).AsAsyncEnumerable())
            {
                if (id == wanted) return new() { Actor = actor, EntryId = wanted, Offset = index / 100 * 100 };
                index++;
            }
        }
        return new() { Actor = actor, Offset = saved.Offset };
    }

    public async Task SaveInboxPositionAsync(string actor, Guid? entryId, int offset)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var saved = await db.InboxPositions.SingleOrDefaultAsync(x => x.Actor == actor);
            if (saved == null) { saved = new() { Actor = actor }; db.InboxPositions.Add(saved); }
            saved.EntryId = entryId;
            saved.Offset = Math.Max(0, offset) / 100 * 100;
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<DocumentRescan> RescanDocumentAsync(Guid entryId, string actor, string expectedSourcePath)
    {
        Actor(actor);
        var superseded = new List<Guid>();
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var (entry, root) = await WritableEntryAsync(db, entryId);
            await RequireIntakeAsync(db, entry);
            if (entry.RelativePath != expectedSourcePath) throw new StorageException("This document moved. Refresh the inbox before rescanning it.");
            var path = StoragePaths.Resolve(root.Location, entry.RelativePath);
            var info = new FileInfo(path);
            if (!info.Exists) throw new StorageException("The document is unavailable. Its existing index has been kept; check storage.");
            var changed = info.Length != entry.Size || FileTimestamp(info.LastWriteTimeUtc) != entry.ModifiedAt;
            if (changed)
            {
                entry.Size = info.Length; entry.ModifiedAt = FileTimestamp(info.LastWriteTimeUtc);
                entry.ObservedAt = time.GetUtcNow();
            }
            if (entry.Size == 0 || time.GetUtcNow() - entry.ObservedAt < StabilityDelay)
            {
                entry.Readiness = entry.Size == 0 ? "Empty file" : "Finishing scan";
                entry.TextStatus = "Pending";
            }
            else
            {
                entry.ContentHash = await FingerprintAsync(root, entry); entry.Readiness = "Ready";
                await QueueExtractionAsync(db, entry, force: true);
            }
            entry.ExtractedText = ""; entry.TextHash = ""; entry.PassageRevision = ""; entry.ExtractionArtifactJson = "";
            // OCR can change while the PDF bytes stay identical. Old proposals must never survive that.
            foreach (var job in await db.IdentificationJobs.Where(x => x.EntryId == entry.Id &&
                (x.State == "Queued" || x.State == "Running" || x.State == "Retry" || x.State == "Succeeded")).ToListAsync())
            {
                job.State = "Superseded"; job.LastError = "Text was rescanned. Read with AI again for a fresh suggestion.";
                job.RunId = null; job.LeaseUntil = null; job.CompletedAt = time.GetUtcNow(); superseded.Add(job.Id);
            }
            await db.SaveChangesAsync();

        }
        finally { writer.Release(); foreach (var id in superseded) CancelLiveIdentification(id); }
        if (!deferExtraction) await DrainExtractionAsync();
        var current = await EntryAsync(entryId) ?? throw new StorageException("That document is no longer available.");
        return new(current.TextStatus, current.ExtractedText.Length, current.Readiness);
    }
}
