using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

// Documents already in the Library get details the same way new ones do: a sorting reading,
// reviewed by a person. Accepting saves the details where the file is; "and move" also lets
// it follow its kind's patterns and re-files it. Nothing moves without that choice.
public sealed record BackfillStatus(int Candidates, int Waiting, int Reading, int Ready, bool ReaderOn);
public sealed record BackfillSuggestion(SortDocument Document, string FolderPath, SortChoice Choice, Dictionary<string, string> Sources, string[] Notes,
    string Confidence, Guid JobId, SortPlan WouldGo, bool DescriptiveName);

public sealed partial class LibraryService
{
    public const int BackfillBatch = 100;

    private async Task<IQueryable<CatalogEntry>> BackfillCandidatesAsync(CatalogDb db)
    {
        var folders = await db.Entries.AsNoTracking().Where(x => x.IsFolder && !x.IsMissing && x.TrashBatchId == null).ToListAsync();
        var byId = folders.ToDictionary(x => x.Id);
        var intake = folders.Where(x => HasIntakeAncestor(x, byId)).Select(x => x.Id).ToArray();
        return db.Entries.Where(x => !x.IsFolder && !x.IsMissing && x.TrashBatchId == null && x.TextStatus == "Indexed" && x.ParentId != null && !intake.Contains(x.ParentId.Value)
            && !db.Details.Any(d => d.EntryId == x.Id)
            && !db.IdentificationJobs.Any(j => j.EntryId == x.Id && j.Backfill && (j.State == "Queued" || j.State == "Retry" || j.State == "Running" || j.State == "Succeeded" || j.State == "Rejected")));
    }

    public async Task<BackfillStatus> BackfillStatusAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var settings = await db.OllamaConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
        var candidates = await (await BackfillCandidatesAsync(db)).CountAsync();
        var jobs = db.IdentificationJobs.Where(x => x.Backfill && !db.Details.Any(d => d.EntryId == x.EntryId));
        return new(candidates, await jobs.CountAsync(x => x.State == "Queued" || x.State == "Retry"), await jobs.CountAsync(x => x.State == "Running"),
            await jobs.CountAsync(x => x.State == "Succeeded"), settings?.Enabled == true);
    }

    // Queues readings for up to a batch of Library documents without details.
    public async Task<int> QueueBackfillAsync(string actor, int max = BackfillBatch, Guid? folderId = null)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var settings = await AiDefaultsAsync(db);
            if (!settings.Enabled) throw new StorageException("Turn on the AI reader in Settings first.");
            var candidates = await BackfillCandidatesAsync(db);
            if (folderId != null)
            {
                // Only this folder and what's inside it.
                var folder = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == folderId && x.IsFolder) ?? throw new StorageException("That folder is no longer in the catalog.");
                var prefix = folder.RelativePath.Length == 0 ? "" : folder.RelativePath + "/";
                candidates = candidates.Where(x => x.RootId == folder.RootId && x.RelativePath.StartsWith(prefix));
            }
            var entries = await candidates.OrderBy(x => x.RelativePath).ThenBy(x => x.Id).Take(Math.Clamp(max, 1, BackfillBatch)).ToListAsync();
            var roots = await db.Roots.ToDictionaryAsync(x => x.Id);
            var queued = 0;
            foreach (var entry in entries)
            {
                var root = roots[entry.RootId];
                if (!root.IsOnline || entry.Size == 0) continue;
                try { entry.ContentHash = await FingerprintAsync(root, entry); }
                catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException) { continue; }
                var context = (await SortingContextAsync(db, entry, settings)) with { Backfill = true };
                await EnqueueIdentificationAsync(db, entry, settings, context, actor, false);
                queued++;
            }
            await db.SaveChangesAsync();
            return queued;
        }
        finally { writer.Release(); }
    }

    public async Task<List<BackfillSuggestion>> BackfillSuggestionsAsync(int take = 50)
    {
        await using var db = await factory.CreateDbContextAsync();
        var jobs = await db.IdentificationJobs.AsNoTracking().Where(x => x.Backfill && x.State == "Succeeded" && !db.Details.Any(d => d.EntryId == x.EntryId))
            .OrderBy(x => x.CompletedAt).ThenBy(x => x.Id).Take(Math.Clamp(take, 1, 200)).ToListAsync();
        var vocab = await VocabAsync(db);
        var roots = await db.Roots.AsNoTracking().ToDictionaryAsync(x => x.Id);
        var folderCache = new Dictionary<Guid, List<CatalogEntry>>();
        var result = new List<BackfillSuggestion>();
        foreach (var job in jobs)
        {
            var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.EntryId);
            if (entry == null || entry.IsMissing || entry.TrashBatchId != null || entry.ContentHash != job.ContentHash) continue;
            var reply = StoredReply(job);
            if (reply == null) continue;
            var root = roots[entry.RootId];
            if (!folderCache.TryGetValue(root.Id, out var folders)) folderCache[root.Id] = folders = await FoldersAsync(db, root.Id);
            var hints = Hints(entry, vocab);
            var (choice, sources, _, notes, _) = FromReply(entry, vocab, hints, reply);
            var plan = await PlanAsync(db, root, entry, choice, vocab, folders);
            var parent = entry.RelativePath.Contains('/') ? entry.RelativePath[..entry.RelativePath.LastIndexOf('/')] : "";
            result.Add(new(new(entry.Id, entry.RootId, root.Name, entry.Name, entry.RelativePath, entry.Size, entry.TextStatus, entry.Readiness, entry.ContentHash, entry.ObservedAt, root.IsOnline, entry.ModifiedAt),
                parent, choice, sources, notes.ToArray(), reply.Confidence, job.Id, plan, hints.DescriptiveName));
        }
        return result;
    }

    // Saves the details where the document is. With move, it follows its kind's patterns from now
    // on and is re-filed there through the journal.
    public async Task<string> AcceptBackfillAsync(Guid jobId, SortChoice choice, bool move, string actor)
    {
        Actor(actor);
        if (choice.Kind.Trim().Length == 0) throw new StorageException("Choose what kind of document this is.");
        Guid entryId;
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var job = await db.IdentificationJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.Backfill) ?? throw new StorageException("That suggestion is no longer available.");
            if (job.State != "Succeeded") throw new StorageException("That suggestion was already handled.");
            var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == job.EntryId) ?? throw new StorageException("That document is no longer in the catalog.");
            if (entry.ContentHash != job.ContentHash) throw new StorageException("The document changed since it was read. Read it again.");
            entryId = entry.Id;
            await using var transaction = await db.Database.BeginTransactionAsync();
            var vocab = await VocabAsync(db);
            var reply = StoredReply(job);
            var suggested = reply == null ? null : FromReply(entry, vocab, Hints(entry, vocab), reply).Choice;
            var (kind, sender) = await SaveDetailsCoreAsync(db, entry.Id, choice, actor, reply?.KindDescription ?? "",
                followsFolder: move, followsName: move && !choice.KeepName && choice.NameOverride.Trim().Length == 0, confirmed: true, readerWords: reply?.What ?? "");
            if (suggested != null)
            {
                var corrected = Key(suggested.Kind) != Key(kind.Name) || Key(suggested.From) != Key(sender?.Name ?? "") || suggested.Date != choice.Date;
                kind.Approved++;
                if (corrected) { kind.Corrected++; kind.Streak = 0; } else kind.Streak++;
                if (sender != null && reply!.From.Length > 0 && Key(reply.From) != Key(sender.Name) && Aliases(sender.AliasesJson).All(x => Key(x) != Key(reply.From)))
                    sender.AliasesJson = JsonSerializer.Serialize(CleanAliases(Aliases(sender.AliasesJson).Append(reply.From), sender.Name));
            }
            await RecordRouteAsync(db, kind, sender, choice, entry.Name, entry.ExtractedText, suggested == null ? null : Key(suggested.Kind) != Key(kind.Name) || Key(suggested.From) != Key(sender?.Name ?? "") || suggested.Date != choice.Date);
            job.State = "Filed";
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        finally { writer.Release(); }
        if (!move) return "Details saved. The file stays where it is.";
        var item = (await RefilePlanAsync(entryId: entryId)).FirstOrDefault();
        if (item == null) return "Details saved. It is already where its kind puts it.";
        if (item.Problem != null) return "Details saved. It could not move: " + item.Problem;
        await RefileAsync(entryId, item.FromPath, item.ToPath, actor);
        return "Details saved and moved to " + item.ToPath + ".";
    }

    public async Task DismissBackfillAsync(Guid jobId, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var job = await db.IdentificationJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.Backfill) ?? throw new StorageException("That suggestion is no longer available.");
            if (job.State == "Succeeded") { job.State = "Rejected"; job.LastError = "Skipped in Tidy up."; job.CompletedAt = time.GetUtcNow(); await db.SaveChangesAsync(); }
        }
        finally { writer.Release(); }
    }
}
