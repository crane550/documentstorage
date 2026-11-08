using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

public sealed record IntakeItem(CatalogEntry Entry, DocumentReview? Review, string Status);
public sealed record IntakePage(List<IntakeItem> Items, int Total);
public sealed record RuleInput(Guid? Id, int ExpectedRevision, string Name, string DocumentType, Guid DestinationFolderId,
    Dictionary<string,string> Match, string FilenameTemplate, string[] RequiredFacts, bool Enabled);
public sealed record FilingProposal(bool Ready, string Reason, Guid? PreviewId = null, string? DestinationPath = null,
    string? Filename = null, string? RuleName = null, int? RuleRevision = null, Guid? MatchedRuleId = null);
public sealed record RuleSimulation(Guid EntryId, string Name, FilingProposal Proposal);

public sealed partial class LibraryService
{
    public static readonly TimeSpan StabilityDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> BuiltInFacts = ["extension", "original_stem"];

    private static bool HasIntakeAncestor(CatalogEntry entry, IEnumerable<CatalogEntry> folders)
        => HasIntakeAncestor(entry, folders.Where(x => x.IsFolder).ToDictionary(x => x.Id));

    private static bool HasIntakeAncestor(CatalogEntry entry, IReadOnlyDictionary<Guid,CatalogEntry> byId)
    {
        var current = entry.IsFolder ? entry : byId.GetValueOrDefault(entry.ParentId ?? Guid.Empty);
        while (current != null)
        {
            if (current.IsIntake && !current.IsMissing && current.TrashBatchId == null) return true;
            current = byId.GetValueOrDefault(current.ParentId ?? Guid.Empty);
        }
        return false;
    }

    private static async Task<IQueryable<CatalogEntry>> IntakeQueryAsync(CatalogDb db)
    {
        var folders = await db.Entries.AsNoTracking().Where(x => x.IsFolder && !x.IsMissing && x.TrashBatchId == null).ToListAsync();
        var byId = folders.ToDictionary(x => x.Id);
        var parents = folders.Where(x => HasIntakeAncestor(x, byId)).Select(x => x.Id).ToArray();
        return db.Entries.Where(x => !x.IsFolder && !x.IsMissing && x.TrashBatchId == null && x.ParentId != null && parents.Contains(x.ParentId.Value));
    }

    public async Task<IntakePage> IntakeAsync(int offset = 0)
    {
        await using var db = await factory.CreateDbContextAsync();
        var query = (await IntakeQueryAsync(db)).AsNoTracking();
        var entries = await query.OrderBy(x => x.ObservedAt).ThenBy(x => x.Name).ThenBy(x => x.Id).Skip(Math.Max(0, offset)).Take(100).ToListAsync();
        var ids = entries.Select(x => x.Id).ToArray();
        var reviews = await db.Reviews.AsNoTracking().Where(x => ids.Contains(x.EntryId)).ToDictionaryAsync(x => x.EntryId);
        var roots = await db.Roots.AsNoTracking().ToDictionaryAsync(x => x.Id);
        return new(entries.Select(entry =>
        {
            var review = reviews.GetValueOrDefault(entry.Id);
            var status = !roots[entry.RootId].IsOnline ? "Storage offline" : entry.Readiness != "Ready" ? entry.Readiness :
                review == null ? "Needs identification" : review.ContentHash != entry.ContentHash ? "Document changed" :
                review.Reviewed ? "Reviewed" : "Needs review";
            return new IntakeItem(entry, review, status);
        }).ToList(), await query.CountAsync());
    }

    public async Task SetIntakeAsync(Guid folderId, bool enabled)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var folder = await db.Entries.SingleOrDefaultAsync(x => x.Id == folderId);
            if (folder == null || !folder.IsFolder || folder.ParentId == null || folder.IsMissing || folder.TrashBatchId != null)
                throw new StorageException("Choose an available subfolder inside a storage location for intake.");
            var all = await db.Entries.Where(x => x.RootId == folder.RootId && !x.IsMissing && x.TrashBatchId == null).ToListAsync();
            bool Inside(CatalogEntry candidate, CatalogEntry parent) => candidate.Id == parent.Id || candidate.RelativePath.StartsWith(parent.RelativePath + "/", StoragePaths.Comparison);
            if (enabled)
            {
                if (all.Any(x => x.IsIntake && x.Id != folderId && (Inside(x, folder) || Inside(folder, x))))
                    throw new StorageException("Intake folders cannot overlap. Choose a folder outside the existing intake folders.");
                var descendants = all.Where(x => x.IsFolder && Inside(x, folder)).Select(x => x.Id).ToArray();
                if (await db.FilingRules.AnyAsync(x => x.Enabled && descendants.Contains(x.DestinationFolderId)))
                    throw new StorageException("An enabled filing rule targets this folder. Change or pause that rule before using it for intake.");
            }
            if (folder.IsIntake == enabled) return;
            folder.IsIntake = enabled;
            foreach (var file in all.Where(x => !x.IsFolder && Inside(x, folder)))
            {
                file.ObservedAt = time.GetUtcNow();
                // Changing a folder's role does not change its bytes. Retain the extraction binding;
                // the next settled scan still fingerprints the file and detects real changes.
                file.Readiness = enabled ? "Finishing scan" : "Not queued";
            }
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<DocumentReview?> ReviewAsync(Guid entryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Reviews.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entryId);
    }

    public async Task<DocumentReview> SaveReviewAsync(Guid entryId, int expectedRevision, string documentType,
        Dictionary<string,string> facts, bool reviewed, string actor)
    {
        Actor(actor);
        documentType = FactKey(documentType);
        facts = NormalizeFacts(facts);
        if (facts.Keys.Any(BuiltInFacts.Contains)) throw new StorageException("Extension and original_stem are read from the file and cannot be edited.");
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var (entry, root) = await WritableEntryAsync(db, entryId);
            await RequireIntakeAsync(db, entry);
            RequireStable(entry);
            var hash = await FingerprintAsync(root, entry);
            var review = await db.Reviews.SingleOrDefaultAsync(x => x.EntryId == entryId);
            if ((review?.Revision ?? 0) != expectedRevision) throw new StorageException("This review changed in another tab. Refresh before saving.");
            if (review == null) { review = new DocumentReview { EntryId = entryId }; db.Reviews.Add(review); }
            review.DocumentType = documentType;
            review.FactsJson = JsonSerializer.Serialize(facts);
            review.ContentHash = hash;
            review.Reviewed = reviewed;
            review.Revision++;
            review.UpdatedAt = time.GetUtcNow();
            review.UpdatedBy = actor;
            entry.ContentHash = hash;
            entry.Readiness = "Ready";
            await db.SaveChangesAsync();
            return review;
        }
        finally { writer.Release(); }
    }

    public async Task<List<FilingRuleDefinition>> RulesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.FilingRules.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    }

    public async Task<List<FilingRuleRevision>> RuleHistoryAsync(Guid ruleId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.RuleRevisions.AsNoTracking().Where(x => x.RuleId == ruleId).OrderByDescending(x => x.Revision).ToListAsync();
    }

    public async Task<FilingRuleDefinition> SaveRuleAsync(RuleInput input, string actor)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            return await SaveRuleLockedAsync(db, input, actor);
        }
        finally { writer.Release(); }
    }

    private async Task<FilingRuleDefinition> SaveRuleLockedAsync(CatalogDb db, RuleInput input, string actor)
    {
        Actor(actor);
        var name = input.Name.Trim();
        if (name.Length is 0 or > 100) throw new StorageException("Enter a rule name of 100 characters or less.");
        var type = FactKey(input.DocumentType);
        var match = NormalizeFacts(input.Match);
        var required = input.RequiredFacts.Select(FactKey).Distinct(StringComparer.Ordinal).ToArray();
        if (required.Length > 32) throw new StorageException("A rule can require up to 32 facts.");
        FilingRules.ValidateTemplate(input.FilenameTemplate);
        var destination = await db.Entries.SingleOrDefaultAsync(x => x.Id == input.DestinationFolderId);
        if (destination == null || !destination.IsFolder || destination.IsMissing || destination.TrashBatchId != null)
            throw new StorageException("Choose an available destination folder.");
        var folders = await db.Entries.AsNoTracking().Where(x => x.IsFolder && x.RootId == destination.RootId).ToListAsync();
        if (HasIntakeAncestor(destination, folders)) throw new StorageException("The destination must be outside intake so filed documents leave the queue.");
        var rule = input.Id == null ? null : await db.FilingRules.SingleOrDefaultAsync(x => x.Id == input.Id);
        if (input.Id != null && rule == null) throw new StorageException("That rule no longer exists. Refresh the rules list.");
        if ((rule?.Revision ?? 0) != input.ExpectedRevision) throw new StorageException("This rule changed in another tab. Reload it before saving.");
        if (rule == null) { rule = new FilingRuleDefinition(); db.FilingRules.Add(rule); }
        rule.Name = name;
        rule.DocumentType = type;
        rule.DestinationFolderId = destination.Id;
        rule.MatchJson = JsonSerializer.Serialize(match);
        rule.FilenameTemplate = input.FilenameTemplate.Trim();
        rule.RequiredFactsJson = JsonSerializer.Serialize(required);
        rule.Enabled = input.Enabled;
        rule.Revision++;
        rule.UpdatedAt = time.GetUtcNow();
        rule.UpdatedBy = actor;
        db.RuleRevisions.Add(new FilingRuleRevision
        {
            RuleId = rule.Id, Revision = rule.Revision, SnapshotJson = JsonSerializer.Serialize(rule),
            CreatedAt = rule.UpdatedAt, CreatedBy = actor
        });
        await db.SaveChangesAsync();
        return rule;
    }

    public async Task<FilingProposal> PreviewFilingAsync(Guid entryId, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == entryId);
            if (entry == null) return new(false, "This document is no longer available.");
            var rules = await db.FilingRules.Where(x => x.Enabled).ToListAsync();
            var proposal = await CandidateAsync(db, entry, rules);
            if (!proposal.Ready) return proposal;
            var review = await db.Reviews.SingleAsync(x => x.EntryId == entryId);
            var rule = rules.Single(x => x.Id == proposal.MatchedRuleId);
            var snapshot = new FilingPreview
            {
                EntryId = entryId, RuleId = rule.Id, RuleRevision = rule.Revision, ReviewRevision = review.Revision,
                DestinationFolderId = rule.DestinationFolderId, SourcePath = entry.RelativePath, DestinationPath = proposal.DestinationPath!,
                Filename = proposal.Filename!, ContentHash = review.ContentHash, CreatedAt = time.GetUtcNow(),
                ExpiresAt = time.GetUtcNow() + PreviewLifetime, CreatedBy = actor
            };
            db.FilingPreviews.Add(snapshot);
            await db.SaveChangesAsync();
            return proposal with { PreviewId = snapshot.Id };
        }
        finally { writer.Release(); }
    }

    public async Task<List<RuleSimulation>> SimulateRuleAsync(Guid ruleId)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var selected = await db.FilingRules.SingleOrDefaultAsync(x => x.Id == ruleId) ?? throw new StorageException("Save the rule before testing it.");
            var rules = await db.FilingRules.Where(x => x.Enabled && x.Id != ruleId).ToListAsync();
            rules.Add(selected); // Simulate a draft without activating it or creating an executable preview.
            var entries = await (await IntakeQueryAsync(db)).OrderBy(x => x.ObservedAt).ThenBy(x => x.Name).ThenBy(x => x.Id).Take(25).ToListAsync();
            var result = new List<RuleSimulation>();
            foreach (var entry in entries)
            {
                var proposal = await CandidateAsync(db, entry, rules);
                if (proposal.Ready && proposal.MatchedRuleId != ruleId) proposal = new(false, "This rule does not match this document.");
                result.Add(new(entry.Id, entry.Name, proposal));
            }
            return result;
        }
        finally { writer.Release(); }
    }

    public async Task<Guid> ApplyFilingAsync(Guid previewId, string actor, Guid? connectionId = null, Guid? assistantProposalId = null)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var preview = await db.FilingPreviews.SingleOrDefaultAsync(x => x.Id == previewId) ?? throw new StorageException("Create a filing preview first.");
            if (assistantProposalId != null) await CheckAssistantFilingAsync(db, previewId, assistantProposalId.Value, new(actor, connectionId));
            else if (connectionId != null) throw new StorageException("An approved assistant proposal is required.");
            if (preview.CreatedBy != actor) throw new StorageException("This preview belongs to another sign-in. Create your own preview.");
            if (preview.State == "Applied" && preview.OperationId is Guid completed) return completed;
            if (preview.OperationId != null) throw new StorageException("The previous attempt needs recovery or a new preview. Rescan storage and check Activity.");
            if (preview.ExpiresAt <= time.GetUtcNow()) throw new StorageException("This preview expired. Preview the document again.");
            var (entry, root) = await WritableEntryAsync(db, preview.EntryId);
            var rule = await db.FilingRules.SingleOrDefaultAsync(x => x.Id == preview.RuleId);
            var review = await db.Reviews.SingleOrDefaultAsync(x => x.EntryId == entry.Id);
            if (rule == null || !rule.Enabled || rule.Revision != preview.RuleRevision || review?.Revision != preview.ReviewRevision ||
                entry.RelativePath != preview.SourcePath || review.ContentHash != preview.ContentHash)
                throw new StorageException("The rule, review or document location changed. Create a new preview.");
            // Re-evaluate ALL current rules: a newly enabled conflicting rule must invalidate an old preview too.
            var candidate = await CandidateAsync(db, entry, await db.FilingRules.Where(x => x.Enabled).ToListAsync());
            if (!candidate.Ready || candidate.MatchedRuleId != preview.RuleId || candidate.DestinationPath != preview.DestinationPath || candidate.Filename != preview.Filename)
                throw new StorageException(candidate.Ready ? "The destination changed. Create a new preview." : candidate.Reason);
            await using var source = OpenFingerprintStream(root, entry);
            var hash = await HashStreamAsync(source, root, entry);
            if (hash != preview.ContentHash) throw new StorageException("The document content changed. Rescan and review its facts again.");
            var operation = new FileOperation
            {
                Kind = "File", RootId = root.Id, EntryId = entry.Id, FromPath = entry.RelativePath,
                ToPath = preview.DestinationPath, TargetParentId = preview.DestinationFolderId, FilingPreviewId = preview.Id,
                CreatedAt = time.GetUtcNow()
            };
            preview.OperationId = operation.Id;
            await ExecuteMoveAsync(db, root, entry, operation);
            return operation.Id;
        }
        finally { writer.Release(); }
    }

    private async Task<FilingProposal> CandidateAsync(CatalogDb db, CatalogEntry entry, List<FilingRuleDefinition> rules)
    {
        try
        {
            var (_, root) = await WritableEntryAsync(db, entry.Id);
            await RequireIntakeAsync(db, entry);
            RequireStable(entry);
            var review = await db.Reviews.SingleOrDefaultAsync(x => x.EntryId == entry.Id);
            if (review == null) return new(false, "Identify the document and enter its facts first.");
            if (!review.Reviewed) return new(false, "Check the facts against the document and mark them reviewed.");
            if (await FingerprintAsync(root, entry) != review.ContentHash) return new(false, "The document content changed. Rescan and review its facts again.");
            var facts = JsonSerializer.Deserialize<Dictionary<string,string>>(review.FactsJson)!;
            facts["extension"] = Path.GetExtension(entry.Name).TrimStart('.');
            facts["original_stem"] = Path.GetFileNameWithoutExtension(entry.Name);
            var plan = FilingRules.Preview(new(review.DocumentType, false, facts), rules.Select(ToEngine));
            if (!plan.Ready) return new(false, plan.Reason);
            var rule = rules.Single(x => x.Id == plan.RuleId);
            var (destination, destinationRoot) = await WritableEntryAsync(db, plan.FolderId!.Value);
            if (!destination.IsFolder || destination.TrashBatchId != null) return new(false, "The destination folder is unavailable.");
            var folders = await db.Entries.AsNoTracking().Where(x => x.IsFolder && x.RootId == destination.RootId).ToListAsync();
            if (HasIntakeAncestor(destination, folders)) return new(false, "Choose a destination outside intake.");
            if (root.Id != destinationRoot.Id) return new(false, "Filing between storage locations is not available yet. Choose a destination in the same storage location.");
            if (!Directory.Exists(StoragePaths.Resolve(root.Location, destination.RelativePath))) return new(false, "The destination folder is unavailable. Rescan storage.");
            if (!string.Equals(Path.GetExtension(entry.Name), Path.GetExtension(plan.Filename), StringComparison.OrdinalIgnoreCase))
                return new(false, "The filename must preserve the original extension. Use {extension} in the template.");
            var path = StoragePaths.Join(destination.RelativePath, plan.Filename!);
            if (StoragePaths.Exists(StoragePaths.Resolve(root.Location, path)) || await db.Entries.AnyAsync(x => x.RootId == root.Id && x.RelativePath == path))
                return new(false, "The destination name is already in use. Nothing will be overwritten.");
            return new(true, "Reviewed facts match one rule. Confirm the name and destination before filing.", DestinationPath: path,
                Filename: plan.Filename, RuleName: rule.Name, RuleRevision: rule.Revision, MatchedRuleId: rule.Id);
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        { return new(false, ex is StorageException ? ex.Message : "The file or destination is unavailable. Check storage and rescan."); }
    }

    private static FilingRule ToEngine(FilingRuleDefinition rule) => new(rule.Id, rule.DocumentType, rule.DestinationFolderId,
        JsonSerializer.Deserialize<Dictionary<string,string>>(rule.MatchJson)!, rule.FilenameTemplate, JsonSerializer.Deserialize<string[]>(rule.RequiredFactsJson)!);

    private static async Task RequireIntakeAsync(CatalogDb db, CatalogEntry entry)
    {
        if (entry.IsFolder || entry.IsMissing || entry.TrashBatchId != null ||
            !HasIntakeAncestor(entry, await db.Entries.AsNoTracking().Where(x => x.RootId == entry.RootId && x.IsFolder).ToListAsync()))
            throw new StorageException("Choose a document currently inside an intake folder.");
    }

    private void RequireStable(CatalogEntry entry)
    {
        if (entry.Size == 0) throw new StorageException("This file is empty. Wait for the scanner to finish writing it.");
        if (time.GetUtcNow() - entry.ObservedAt < StabilityDelay)
            throw new StorageException("This scan is still settling. Wait five seconds, then rescan storage before reviewing or filing.");
        if (entry.Readiness != "Ready") throw new StorageException("This file is not ready. Rescan storage before reviewing or filing.");
    }

    private static FileStream OpenFingerprintStream(StorageRoot root, CatalogEntry entry)
    {
        var path = StoragePaths.Resolve(root.Location, entry.RelativePath);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != entry.Size || FileTimestamp(info.LastWriteTimeUtc) != entry.ModifiedAt)
            throw new StorageException("The document changed on disk. Rescan and review its facts again.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    private static async Task<string> HashStreamAsync(FileStream stream, StorageRoot root, CatalogEntry entry)
    {
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        var info = new FileInfo(StoragePaths.Resolve(root.Location, entry.RelativePath));
        if (!info.Exists || info.Length != entry.Size || FileTimestamp(info.LastWriteTimeUtc) != entry.ModifiedAt)
            throw new StorageException("The scan changed while it was read. Wait, then rescan storage.");
        return hash;
    }

    private static async Task<string> FingerprintAsync(StorageRoot root, CatalogEntry entry)
    {
        await using var stream = OpenFingerprintStream(root, entry);
        return await HashStreamAsync(stream, root, entry);
    }

    // PostgreSQL stores microseconds; NTFS and Linux can report finer timestamps.
    private static DateTimeOffset FileTimestamp(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    private static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256) throw new StorageException("Sign in again before changing filing settings.");
    }

    private static string FactKey(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(value, "^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant))
            throw new StorageException("Use a short document type or fact name with lowercase letters, numbers and underscores, such as bank_statement.");
        return value;
    }

    private static Dictionary<string,string> NormalizeFacts(Dictionary<string,string> pairs)
    {
        if (pairs.Count > 32) throw new StorageException("Use up to 32 facts or match conditions.");
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var key = FactKey(pair.Key);
            var value = pair.Value.Trim();
            if (value.Length is 0 or > 1000) throw new StorageException("Each fact needs a value of 1 to 1,000 characters.");
            if (!result.TryAdd(key, value)) throw new StorageException("Each fact name must appear only once.");
        }
        return result;
    }
}
