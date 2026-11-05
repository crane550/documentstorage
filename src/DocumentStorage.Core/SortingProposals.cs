using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

// What an outside assistant (Claude, ChatGPT over MCP) may suggest about sorting. Each is only a
// proposal: the person accepts it in the browser, and acceptance runs the same services the pages
// use. An assistant can't approve, can't switch a kind to file by itself, and can't move a file
// except through an accepted re-file of documents whose kind's patterns already put them elsewhere.
public sealed record DetailsProposal(Guid DocumentId, string Kind, string From, string Date, string Title, string[] Tags, Dictionary<string, string> Fields);
public sealed record KindProposal(Guid? KindId, string Name, string Description, string FolderPattern, string NamePattern, string[] Fields);
public sealed record MergeProposal(string What, Guid FromId, Guid IntoId);
public sealed record RefileProposal(Guid[] DocumentIds);

public sealed partial class LibraryService
{
    public static readonly string[] SortingProposalKinds = ["Details", "Kind", "Merge", "Refile"];

    private static DateOnly? ProposalDate(string date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        return DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", out var value) ? value : throw new StorageException("Use dates as yyyy-MM-dd.");
    }

    // Checks a sorting proposal with the same rules the pages use, and returns a short summary for review.
    private async Task<string> ValidateSortingProposalAsync(CatalogDb db, string kind, string payloadJson)
    {
        switch (kind)
        {
            case "Details":
            {
                var input = JsonSerializer.Deserialize<DetailsProposal>(payloadJson) ?? throw new StorageException("Details are missing.");
                var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.DocumentId && !x.IsFolder && !x.IsMissing && x.TrashBatchId == null)
                    ?? throw new StorageException("Document not found.");
                KindName(input.Kind); if (input.From.Trim().Length > 0) SenderName(input.From);
                ProposalDate(input.Date);
                if (input.Title.Length > 200 || input.Tags.Length > 12 || input.Fields.Count > 16 || input.Fields.Values.Any(x => x.Length > 300)) throw new StorageException("Keep the title short, with up to 12 tags and 16 fields.");
                foreach (var tag in input.Tags) TagName(tag);
                foreach (var field in input.Fields.Keys) FactKey(field);
                return JsonSerializer.Serialize(new { document = entry.Name, path = entry.RelativePath });
            }
            case "Kind":
            {
                var input = JsonSerializer.Deserialize<KindProposal>(payloadJson) ?? throw new StorageException("Kind is missing.");
                var name = KindName(input.Name);
                SortPatterns.ValidatePattern(input.FolderPattern, folder: true); SortPatterns.ValidatePattern(input.NamePattern, folder: false);
                if (input.Description.Length > 400 || input.Fields.Length > 12) throw new StorageException("Keep the description under 400 characters and up to 12 fields.");
                foreach (var field in input.Fields) FactKey(field);
                if (input.KindId is Guid id && !await db.Kinds.AnyAsync(x => x.Id == id)) throw new StorageException("Kind not found.");
                if (await db.Kinds.AnyAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != input.KindId)) throw new StorageException("There is already a kind called " + name + ".");
                var values = new SortValues(name, "Acme Co", new DateOnly(2026, 10, 14), "Example title", input.Fields.ToDictionary(x => x, x => x.Replace('_', ' ')));
                return JsonSerializer.Serialize(new { example = string.Join('/', SortPatterns.Folder(input.FolderPattern, values).Append(SortPatterns.Name(input.NamePattern, values, "scan.pdf"))) });
            }
            case "Merge":
            {
                var input = JsonSerializer.Deserialize<MergeProposal>(payloadJson) ?? throw new StorageException("Merge is missing.");
                if (input.FromId == input.IntoId) throw new StorageException("Choose two different items to merge.");
                if (input.What == "sender")
                {
                    var names = await db.Correspondents.Where(x => x.Id == input.FromId || x.Id == input.IntoId).ToDictionaryAsync(x => x.Id, x => x.Name);
                    if (names.Count != 2) throw new StorageException("Sender not found.");
                    return JsonSerializer.Serialize(new { from = names[input.FromId], into = names[input.IntoId] });
                }
                if (input.What == "tag")
                {
                    var names = await db.Tags.Where(x => x.Id == input.FromId || x.Id == input.IntoId).ToDictionaryAsync(x => x.Id, x => x.Name);
                    if (names.Count != 2) throw new StorageException("Tag not found.");
                    return JsonSerializer.Serialize(new { from = names[input.FromId], into = names[input.IntoId] });
                }
                throw new StorageException("Merge senders or tags.");
            }
            case "Refile":
            {
                var input = JsonSerializer.Deserialize<RefileProposal>(payloadJson) ?? throw new StorageException("Documents are missing.");
                if (input.DocumentIds.Length is 0 or > 100) throw new StorageException("Propose 1–100 documents to move.");
                var plans = new List<RefileItem>();
                foreach (var id in input.DocumentIds.Distinct())
                {
                    var item = (await RefilePlanAsync(entryId: id)).FirstOrDefault() ?? throw new StorageException($"Document {id} is already where its kind puts it, or doesn't follow its kind.");
                    if (item.Problem != null) throw new StorageException(item.Name + ": " + item.Problem);
                    plans.Add(item);
                }
                return JsonSerializer.Serialize(plans.Select(x => new { document_id = x.EntryId, name = x.Name, from = x.FromPath, to = x.ToPath }));
            }
        }
        throw new StorageException("Unsupported proposal kind.");
    }

    // Accepting runs outside the proposal's own lock (the services take the writer themselves):
    // claim it as Applying, apply, then record the result. A failure puts it back to Pending.
    private async Task DecideSortingProposalAsync(Guid id, int expectedRevision, bool accept, string actor)
    {
        AssistantProposal proposal;
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            proposal = await db.AssistantProposals.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == actor) ?? throw new StorageException("Proposal not found.");
            if (proposal.State == "Applied" && accept) return;
            if (proposal.State == "Rejected" && !accept) return;
            if (proposal.Revision != expectedRevision || proposal.State != "Pending") throw new StorageException("This proposal changed. Refresh before deciding.");
            if (!accept)
            {
                proposal.State = "Rejected"; proposal.CompletedAt = time.GetUtcNow(); proposal.Revision++;
                Audit(db, new(actor, proposal.ConnectionId), "proposal.reject", proposal.State, proposal.Id); await db.SaveChangesAsync(); return;
            }
            await CheckCallerAsync(db, new(actor, proposal.ConnectionId), ProposalScope(proposal.Kind));
            if (proposal.ExpiresAt <= time.GetUtcNow()) throw new StorageException("This proposal expired. Ask for a new one.");
            proposal.State = "Applying"; proposal.Revision++;
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }

        object result; string? failure = null;
        try { result = await ApplySortingProposalAsync(proposal, actor); }
        catch (StorageException ex) { failure = ex.Message; result = new { }; }

        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var saved = await db.AssistantProposals.SingleAsync(x => x.Id == id);
            saved.Revision++;
            if (failure != null) { saved.State = "Pending"; Audit(db, new(actor, saved.ConnectionId), "proposal.accept", "failed", saved.Id); }
            else
            {
                saved.State = "Applied"; saved.CompletedAt = time.GetUtcNow(); saved.ApprovedBy = actor; saved.ResultJson = JsonSerializer.Serialize(result);
                Audit(db, new(actor, saved.ConnectionId), "proposal.accept", saved.State, saved.Id);
            }
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
        if (failure != null) throw new StorageException(failure);
    }

    private async Task<object> ApplySortingProposalAsync(AssistantProposal proposal, string actor)
    {
        switch (proposal.Kind)
        {
            case "Details":
            {
                var input = JsonSerializer.Deserialize<DetailsProposal>(proposal.PayloadJson)!;
                await SaveDocumentDetailsAsync(input.DocumentId, new(input.Kind, input.From, ProposalDate(input.Date), input.Title, input.Tags, input.Fields), actor);
                return new { document_id = input.DocumentId };
            }
            case "Kind":
            {
                var input = JsonSerializer.Deserialize<KindProposal>(proposal.PayloadJson)!;
                DocumentKind? existing = null;
                if (input.KindId is Guid kindId) { await using var db = await factory.CreateDbContextAsync(); existing = await db.Kinds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == kindId) ?? throw new StorageException("Kind not found."); }
                // An assistant never changes whether a kind files itself.
                var saved = await SaveKindAsync(new() { Id = existing?.Id ?? Guid.Empty, Revision = existing?.Revision ?? 0, Name = input.Name, Description = input.Description,
                    FolderPattern = input.FolderPattern, NamePattern = input.NamePattern, FieldsJson = JsonSerializer.Serialize(input.Fields), AutoFile = existing?.AutoFile ?? false }, actor);
                return new { kind_id = saved.Id, documents_to_move = (await RefilePlanAsync(kindId: saved.Id)).Count(x => x.Problem == null) };
            }
            case "Merge":
            {
                var input = JsonSerializer.Deserialize<MergeProposal>(proposal.PayloadJson)!;
                if (input.What == "sender") { await MergeSendersAsync(input.FromId, input.IntoId, actor); return new { into = input.IntoId, documents_to_move = (await RefilePlanAsync(senderId: input.IntoId)).Count(x => x.Problem == null) }; }
                await MergeTagsAsync(input.FromId, input.IntoId, actor); return new { into = input.IntoId };
            }
            case "Refile":
            {
                var input = JsonSerializer.Deserialize<RefileProposal>(proposal.PayloadJson)!;
                var items = new List<RefileItem>();
                foreach (var id in input.DocumentIds.Distinct()) if ((await RefilePlanAsync(entryId: id)).FirstOrDefault() is { Problem: null } item) items.Add(item);
                var outcome = await RefileManyAsync(items, actor);
                return new { moved = outcome.Moved, failed = outcome.Failed };
            }
        }
        throw new StorageException("Unsupported proposal kind.");
    }
}
