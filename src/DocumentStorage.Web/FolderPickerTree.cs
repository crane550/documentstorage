using DocumentStorage.Core;
using DocumentStorage.Data;

namespace DocumentStorage.Web;

public sealed record FolderSelection(Guid Id, Guid RootId, string Path);
public sealed record FolderPickerRow(CatalogEntry Folder, int Depth, bool HasChildren, bool Match);

// A view of catalog folders only. The writer still rechecks paths before changing storage.
public sealed class FolderPickerTree
{
    public Dictionary<Guid, CatalogEntry> Folders { get; }
    public Dictionary<Guid, StorageRoot> Roots { get; }
    public HashSet<Guid> BlockedRoots { get; }
    public HashSet<Guid> Expanded { get; } = [];
    private readonly Dictionary<Guid, List<CatalogEntry>> children;
    private readonly List<CatalogEntry> top;
    public int MatchCount { get; private set; }
    public bool Shortened => MatchCount > 200;

    public FolderPickerTree(LibraryOverview overview, Guid? rootId, Guid? excludedBranch, bool excludeInbox)
    {
        Roots = overview.Roots.ToDictionary(x => x.Id);
        BlockedRoots = (overview.BlockedRootIds ?? []).ToHashSet();
        var all = overview.Folders.ToDictionary(x => x.Id);
        bool Excluded(CatalogEntry folder)
        {
            var seen = new HashSet<Guid>();
            for (CatalogEntry? current = folder; current != null && seen.Add(current.Id); current = current.ParentId is { } id ? all.GetValueOrDefault(id) : null)
                if (current.Id == excludedBranch || excludeInbox && current.IsIntake) return true;
            return false;
        }
        Folders = all.Values.Where(x => !x.IsMissing && x.TrashBatchId == null && (rootId == null || x.RootId == rootId) && !Excluded(x)).ToDictionary(x => x.Id);
        children = Folders.Values.Where(x => x.ParentId != null).GroupBy(x => x.ParentId!.Value)
            .ToDictionary(x => x.Key, x => x.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        top = Folders.Values.Where(x => x.ParentId == null).OrderBy(Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var folder in top) Expanded.Add(folder.Id);
    }

    public string Label(CatalogEntry folder) => (Roots.GetValueOrDefault(folder.RootId)?.Name ?? "Storage") + (folder.RelativePath.Length == 0 ? "" : " / " + folder.RelativePath);
    public bool Writable(CatalogEntry folder) => Roots.GetValueOrDefault(folder.RootId)?.IsOnline == true && !BlockedRoots.Contains(folder.RootId);
    public List<CatalogEntry> Ancestors(Guid id)
    {
        var result = new List<CatalogEntry>();
        var seen = new HashSet<Guid>();
        for (var folder = Folders.GetValueOrDefault(id); folder != null && seen.Add(folder.Id); folder = folder.ParentId is { } parent ? Folders.GetValueOrDefault(parent) : null)
            result.Insert(0, folder);
        return result;
    }
    public void Reveal(Guid id) { foreach (var folder in Ancestors(id)) Expanded.Add(folder.Id); }
    public bool HasChildren(Guid id) => children.GetValueOrDefault(id)?.Count > 0;

    public List<FolderPickerRow> Rows(string query)
    {
        var words = query.Replace('\\', '/').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        HashSet<Guid>? visible = null, matches = null;
        MatchCount = 0;
        if (words.Length > 0)
        {
            var found = Folders.Values.Where(x => words.All(word => Label(x).Contains(word, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => string.Equals(x.Name, query.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            MatchCount = found.Count;
            matches = found.Take(200).Select(x => x.Id).ToHashSet();
            visible = found.Take(200).SelectMany(x => Ancestors(x.Id)).Select(x => x.Id).ToHashSet();
        }
        var rows = new List<FolderPickerRow>();
        var visited = new HashSet<Guid>();
        void Add(CatalogEntry folder, int depth)
        {
            if (!visited.Add(folder.Id) || visible != null && !visible.Contains(folder.Id)) return;
            rows.Add(new(folder, depth, HasChildren(folder.Id), matches?.Contains(folder.Id) == true));
            if (visible != null || Expanded.Contains(folder.Id))
                foreach (var child in children.GetValueOrDefault(folder.Id) ?? []) Add(child, depth + 1);
        }
        foreach (var folder in top) Add(folder, 0);
        return rows;
    }
}
