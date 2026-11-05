using System.Text.RegularExpressions;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

// A filed document whose kind's patterns now put it somewhere else: after a pattern edit, a
// sender rename or merge, or a corrected date. Only the parts that follow the patterns move
// (FollowsFolder, FollowsName); a kept name or a hand-picked folder stays as it is.
public sealed record RefileItem(Guid EntryId, Guid RootId, string RootName, string Name, string FromPath, string ToPath, string[] NewFolders, string Kind, string From, string? Problem);
public sealed record RefileResult(int Moved, List<string> Failed);

public sealed partial class LibraryService
{
    private static readonly Regex Numbered = new(@" \(\d+\)(?=\.[^./]*$|$)", RegexOptions.CultureInvariant);
    // "a (2).pdf" and "a (3).pdf" are the same destination for a move that was shown: the number only avoids a name taken meanwhile.
    private static bool SameDestination(string a, string b) => string.Equals(Numbered.Replace(a, ""), Numbered.Replace(b, ""), StringComparison.Ordinal);

    public const int RefileLimit = 500;

    // What would move, worked out fresh. Nothing is written.
    public async Task<List<RefileItem>> RefilePlanAsync(Guid? kindId = null, Guid? senderId = null, Guid? entryId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var query = db.Details.AsNoTracking().Where(x => x.KindId != null && (x.FollowsFolder || x.FollowsName));
        if (kindId != null) query = query.Where(x => x.KindId == kindId);
        if (senderId != null) query = query.Where(x => x.CorrespondentId == senderId);
        if (entryId != null) query = query.Where(x => x.EntryId == entryId);
        var details = await query.OrderBy(x => x.EntryId).Take(RefileLimit * 4).ToListAsync();
        var vocab = await VocabAsync(db);
        var roots = await db.Roots.AsNoTracking().ToDictionaryAsync(x => x.Id);
        var folderCache = new Dictionary<Guid, List<CatalogEntry>>();
        var items = new List<RefileItem>();
        foreach (var detail in details)
        {
            var entry = await db.Entries.AsNoTracking().Where(x => x.Id == detail.EntryId)
                .Select(x => new CatalogEntry { Id = x.Id, RootId = x.RootId, ParentId = x.ParentId, Name = x.Name, RelativePath = x.RelativePath, IsMissing = x.IsMissing, TrashBatchId = x.TrashBatchId })
                .SingleOrDefaultAsync();
            if (entry == null || entry.IsMissing || entry.TrashBatchId != null) continue;
            if (!folderCache.TryGetValue(entry.RootId, out var folders)) folderCache[entry.RootId] = folders = await FoldersAsync(db, entry.RootId);
            var byId = folders.ToDictionary(x => x.Id);
            if (HasIntakeAncestor(entry, byId)) continue; // Still waiting in an inbox: the Inbox handles it.
            var item = await RefileItemAsync(db, roots[entry.RootId], entry, detail, vocab, folders);
            if (item == null) continue;
            // Two documents headed for the same new name: number the later one, as filing would.
            var taken = items.Where(x => x.RootId == item.RootId).Select(x => x.ToPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (item.Problem == null && taken.Contains(item.ToPath))
            {
                var folder = item.ToPath.Contains('/') ? item.ToPath[..(item.ToPath.LastIndexOf('/') + 1)] : "";
                var stem = Numbered.Replace(Path.GetFileNameWithoutExtension(item.ToPath), ""); var extension = Path.GetExtension(item.ToPath);
                for (var n = 2; taken.Contains(item.ToPath); n++) item = item with { ToPath = $"{folder}{stem} ({n}){extension}" };
            }
            items.Add(item);
            if (items.Count >= RefileLimit) break;
        }
        return items.OrderBy(x => x.Problem != null).ThenBy(x => x.ToPath, StringComparer.Ordinal).ToList();
    }

    private async Task<RefileItem?> RefileItemAsync(CatalogDb db, StorageRoot root, CatalogEntry entry, DocumentDetail detail, Vocab vocab, List<CatalogEntry> folders)
    {
        var view = await DetailViewAsync(db, detail);
        var parentPath = entry.RelativePath.Contains('/') ? entry.RelativePath[..entry.RelativePath.LastIndexOf('/')] : "";
        var choice = new SortChoice(view.Kind, view.From, view.Date, view.Title, view.Tags, view.Fields, KeepName: !detail.FollowsName);
        var plan = await PlanAsync(db, root, entry, choice, vocab, folders,
            detail.FollowsFolder ? null : parentPath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (plan.Problem == null && plan.DestinationPath == entry.RelativePath) return null;
        // The collision check numbers a name that is taken; it's taken by this very document when only
        // the letter case differs, so compare without the number before calling it a move.
        if (plan.Problem == null && string.Equals(plan.DestinationPath, entry.RelativePath, StringComparison.OrdinalIgnoreCase)) return null;
        if (!root.IsOnline) plan = plan with { Problem = "Storage is offline." };
        return new(entry.Id, root.Id, root.Name, entry.Name, entry.RelativePath, plan.DestinationPath, plan.NewFolders, view.Kind, view.From, plan.Problem);
    }

    // Moves one document to where its patterns put it now, if that is still exactly what was shown.
    public async Task RefileAsync(Guid entryId, string expectedFrom, string expectedTo, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var (entry, root) = await WritableEntryAsync(db, entryId);
            if (entry.IsFolder || entry.TrashBatchId != null) throw new StorageException("Choose an available document.");
            if (entry.RelativePath != expectedFrom) throw new StorageException("It has moved since the list was made. Refresh and try again.");
            var detail = await db.Details.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entryId) ?? throw new StorageException("It has no details any more. Refresh and try again.");
            var folders = await FoldersAsync(db, root.Id);
            if (HasIntakeAncestor(entry, folders.ToDictionary(x => x.Id))) throw new StorageException("It is in an inbox. File it from the Inbox.");
            var item = await RefileItemAsync(db, root, entry, detail, await VocabAsync(db), folders) ?? throw new StorageException("It is already where its kind puts it.");
            if (item.Problem != null) throw new StorageException(item.Problem);
            if (!SameDestination(item.ToPath, expectedTo)) throw new StorageException("Its destination changed since the list was made. Refresh and try again.");
            var parentPath = item.ToPath.Contains('/') ? item.ToPath[..item.ToPath.LastIndexOf('/')] : "";
            var existingPath = item.NewFolders.Length == 0 ? parentPath : string.Join('/', parentPath.Split('/')[..^item.NewFolders.Length]);
            var parent = folders.Single(x => x.RelativePath == existingPath);
            // Folders first (each one a plain create that's safe to leave if the move then fails), then the journaled move.
            var parentId = parent.Id; var path = parent.RelativePath;
            foreach (var segment in item.NewFolders)
            {
                path = StoragePaths.Join(path, segment);
                if (StoragePaths.Exists(StoragePaths.Resolve(root.Location, path)) || await db.Entries.AnyAsync(x => x.RootId == root.Id && x.RelativePath == path))
                    throw new StorageException("Something appeared at " + path + ". Rescan storage and try again.");
                Directory.CreateDirectory(StoragePaths.Resolve(root.Location, path));
                var folder = new CatalogEntry { RootId = root.Id, ParentId = parentId, RelativePath = path, Name = segment, IsFolder = true, ModifiedAt = time.GetUtcNow(), ObservedAt = time.GetUtcNow() };
                db.Entries.Add(folder); await db.SaveChangesAsync(); parentId = folder.Id;
            }
            var op = new FileOperation { RootId = root.Id, EntryId = entry.Id, Kind = "Move", FromPath = entry.RelativePath, ToPath = item.ToPath, TargetParentId = parentId };
            await ExecuteMoveAsync(db, root, entry, op);
        }
        finally { writer.Release(); }
    }

    public async Task<RefileResult> RefileManyAsync(IEnumerable<RefileItem> items, string actor)
    {
        var moved = 0; var failed = new List<string>();
        foreach (var item in items)
        {
            try { await RefileAsync(item.EntryId, item.FromPath, item.ToPath, actor); moved++; }
            catch (StorageException ex) { failed.Add(item.Name + ": " + ex.Message); }
        }
        return new(moved, failed);
    }

    // "Keep it where its kind puts it": the Library's switch for one document.
    public async Task SetFollowsPatternsAsync(Guid entryId, bool follow, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var detail = await db.Details.SingleOrDefaultAsync(x => x.EntryId == entryId) ?? throw new StorageException("Give it a kind first.");
            detail.FollowsFolder = follow; detail.FollowsName = follow; detail.Revision++; detail.UpdatedAt = time.GetUtcNow(); detail.UpdatedBy = actor;
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<int> RefileCountAsync(Guid? kindId = null, Guid? senderId = null) => (await RefilePlanAsync(kindId, senderId)).Count(x => x.Problem == null);
}
