using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

/// <summary>A folder where earlier documents like this one now are, and why it is offered.</summary>
public sealed record FolderSuggestion(string Path, int Count, string Reason);

public sealed partial class LibraryService
{
    // Where earlier documents like this one live now (not where they were first filed, so a hand move
    // counts): the same kind from the same sender first, then the same kind, then the same sender.
    public async Task<List<FolderSuggestion>> FolderSuggestionsAsync(Guid entryId, string kind, string from, int take = 3)
    {
        await using var db = await factory.CreateDbContextAsync();
        var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId);
        if (entry == null) return [];
        var vocab = await VocabAsync(db);
        var knownKind = vocab.KindNamed(kind); var knownSender = vocab.Sender(from);
        Guid? kindId = knownKind?.Id, senderId = knownSender?.Id;
        if (kindId == null && senderId == null) return [];
        var folders = await db.Entries.AsNoTracking().Where(x => x.RootId == entry.RootId && x.IsFolder && !x.IsMissing && x.TrashBatchId == null).ToListAsync();
        var byId = folders.ToDictionary(x => x.Id);
        var documents = db.Entries.AsNoTracking().Where(e => e.RootId == entry.RootId && !e.IsFolder && !e.IsMissing && e.TrashBatchId == null && e.ParentId != null && e.Id != entryId);
        var rows = await db.Details.AsNoTracking()
            .Where(d => (kindId != null && d.KindId == kindId) || (senderId != null && d.CorrespondentId == senderId))
            .Join(documents, d => d.EntryId, e => e.Id, (d, e) => new { d.KindId, d.CorrespondentId, Parent = e.ParentId!.Value })
            .ToListAsync();
        return rows
            .Where(r => byId.TryGetValue(r.Parent, out var folder) && folder.RelativePath.Length > 0 && !HasIntakeAncestor(folder, byId))
            .GroupBy(r => r.Parent)
            .Select(group =>
            {
                var both = group.Count(r => kindId != null && senderId != null && r.KindId == kindId && r.CorrespondentId == senderId);
                var sameKind = group.Count(r => kindId != null && r.KindId == kindId);
                var sameSender = group.Count(r => senderId != null && r.CorrespondentId == senderId);
                var rank = both > 0 ? 0 : sameKind > 0 ? 1 : 2;
                var reason = rank == 0 ? $"{knownKind!.Name} from {knownSender!.Name}" : rank == 1 ? knownKind!.Name : "from " + knownSender!.Name;
                return (Path: byId[group.Key].RelativePath, Rank: rank, Count: rank == 0 ? both : rank == 1 ? sameKind : sameSender, Reason: reason);
            })
            .OrderBy(x => x.Rank).ThenByDescending(x => x.Count).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(take, 1, 10))
            .Select(x => new FolderSuggestion(x.Path, x.Count, x.Reason)).ToList();
    }

    // Sends a document back to be filed again. A document still where Stash filed it has that filing
    // undone (its details come back and its kind starts its run again); anything else is moved into
    // this storage's inbox through the ordinary journaled move. Nothing is overwritten.
    public async Task<string> ReturnToInboxAsync(Guid entryId, string actor)
    {
        Actor(actor);
        CatalogEntry entry; Guid? planId, inboxId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId && !x.IsMissing && x.TrashBatchId == null)
                ?? throw new StorageException("That document is no longer in the library.");
            if (entry.IsFolder) throw new StorageException("Only documents go back to the inbox.");
            var folders = await db.Entries.AsNoTracking().Where(x => x.RootId == entry.RootId && x.IsFolder && !x.IsMissing && x.TrashBatchId == null).ToListAsync();
            var byId = folders.ToDictionary(x => x.Id);
            if (entry.ParentId is Guid parentId && byId.TryGetValue(parentId, out var parent) && HasIntakeAncestor(parent, byId))
                throw new StorageException("It is already in the inbox.");
            planId = await db.OrganizationFilings.AsNoTracking()
                .Where(x => x.EntryId == entryId && x.State == "Filed" && x.UndoneAt == null && x.DetailsJson != "" && x.DestinationPath == entry.RelativePath)
                .OrderByDescending(x => x.CompletedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync();
            inboxId = folders.Where(x => x.IsIntake).OrderBy(x => x.RelativePath.Length).ThenBy(x => x.RelativePath, StringComparer.Ordinal)
                .Select(x => (Guid?)x.Id).FirstOrDefault();
        }
        if (planId is Guid id)
        {
            try { await UndoFilingAsync(id, actor); return "Back in the inbox, with its details filled in."; }
            catch (StorageException) when (inboxId != null) { } // The inbox it came from is gone: use this storage's inbox.
        }
        if (inboxId is not Guid target) throw new StorageException("This storage has no inbox folder. Make one in the Library first.");
        await MoveAsync(entryId, target, entry.Name, entry.RelativePath);
        return "Back in the inbox.";
    }
}
