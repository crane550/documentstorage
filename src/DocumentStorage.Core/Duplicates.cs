using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

// Another copy of an inbox document somewhere in Stash. How: "file" (the same bytes), "text" (the
// same text in a different file, such as a PDF downloaded twice), "details" (a filed document of the
// same kind, sender, date and pattern fields) or "name" (the built name is already taken in that
// folder). The first two are certain; the others are likely.
public sealed record DuplicateMatch(Guid EntryId, Guid? FolderId, string RootName, string Path, string How, bool InInbox, bool InTrash)
{
    public bool Certain => How is "file" or "text";
}

public sealed partial class LibraryService
{
    public const int DuplicateTextMinimum = 80;

    // Where else this document already is. Files of the same size whose fingerprint isn't known yet
    // (documents that never came through an inbox) are fingerprinted now, so the Library counts too.
    private async Task<DuplicateMatch[]> DuplicatesAsync(CatalogDb db, CatalogEntry entry, SortChoice choice, Vocab vocab, SortPlan plan)
    {
        if (entry.ContentHash.Length == 0 && entry.ExtractedText.Trim().Length < DuplicateTextMinimum) return [];
        var found = new List<(CatalogEntry Entry, string How)>();
        var others = db.Entries.AsNoTracking().Where(x => !x.IsFolder && !x.IsMissing && x.Id != entry.Id);

        if (entry.ContentHash.Length > 0 && entry.Size > 0)
        {
            var sameSize = await others.Where(x => x.Size == entry.Size).OrderBy(x => x.Id).Take(25).ToListAsync();
            foreach (var other in sameSize.Where(x => x.ContentHash.Length == 0 && x.TrashBatchId == null))
            {
                var root = await db.Roots.AsNoTracking().SingleAsync(x => x.Id == other.RootId);
                if (!root.IsOnline) continue;
                try
                {
                    var hash = await FingerprintAsync(root, other);
                    // Kept only if the file is still as the catalog last saw it; the scanner owns any change.
                    await db.Entries.Where(x => x.Id == other.Id && x.ContentHash == "" && x.Size == other.Size && x.ModifiedAt == other.ModifiedAt)
                        .ExecuteUpdateAsync(x => x.SetProperty(e => e.ContentHash, hash));
                    other.ContentHash = hash;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException) { }
            }
            found.AddRange(sameSize.Where(x => x.ContentHash == entry.ContentHash).Select(x => (x, "file")));
        }

        var text = entry.ExtractedText;
        if (text.Trim().Length >= DuplicateTextMinimum)
        {
            var hash = entry.TextHash.Length > 0 ? entry.TextHash : TextHash(text);
            var sameText = await others.Where(x => x.TextHash == hash).OrderBy(x => x.Id).Take(10).ToListAsync();
            found.AddRange(sameText.Where(x => found.All(f => f.Entry.Id != x.Id)).Select(x => (x, "text")));
        }

        // Filed documents with the same details: the same kind, sender and date, and the same values
        // for every field the kind files or names by (two accounts' statements on one day stay apart).
        var kind = vocab.KindNamed(choice.Kind); var sender = vocab.Sender(choice.From);
        if (kind != null && sender != null && choice.Date is DateOnly date)
        {
            var tokens = SortPatterns.TokensIn(kind.FolderPattern + "/" + kind.NamePattern);
            var keyFields = KindFields(kind).Where(tokens.Contains).ToArray();
            var sameDetails = await db.Details.AsNoTracking().Where(d => d.KindId == kind.Id && d.CorrespondentId == sender.Id && d.DocumentDate == date && d.EntryId != entry.Id)
                .Join(others, d => d.EntryId, e => e.Id, (d, e) => new { Entry = e, d.FieldsJson }).Take(25).ToListAsync();
            foreach (var match in sameDetails)
            {
                var fields = FieldValues(match.FieldsJson);
                if (keyFields.All(k => string.Equals(fields.GetValueOrDefault(k, "").Trim(), choice.Fields.GetValueOrDefault(k, "").Trim(), StringComparison.OrdinalIgnoreCase))
                    && found.All(f => f.Entry.Id != match.Entry.Id))
                    found.Add((match.Entry, "details"));
            }
        }
        if (plan.TakenBy is Guid taken && found.All(f => f.Entry.Id != taken) && await others.SingleOrDefaultAsync(x => x.Id == taken) is { } holder)
            found.Add((holder, "name"));
        if (found.Count == 0) return [];

        var roots = await db.Roots.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name);
        var folders = await db.Entries.AsNoTracking().Where(x => x.IsFolder && !x.IsMissing).ToListAsync();
        var byId = folders.ToDictionary(x => x.Id);
        return found.OrderBy(x => x.How switch { "file" => 0, "text" => 1, "details" => 2, _ => 3 }).ThenBy(x => x.Entry.RelativePath, StringComparer.OrdinalIgnoreCase).Take(5)
            .Select(x => new DuplicateMatch(x.Entry.Id, x.Entry.ParentId, roots.GetValueOrDefault(x.Entry.RootId, ""), x.Entry.RelativePath, x.How,
                x.Entry.TrashBatchId == null && HasIntakeAncestor(x.Entry, byId), x.Entry.TrashBatchId != null)).ToArray();
    }

    public static string TextHash(string text) => text.Length == 0 ? "" : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static Dictionary<string, string> FieldValues(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; } catch (System.Text.Json.JsonException) { return []; }
    }
}
