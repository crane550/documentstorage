using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

public sealed record RecoveryItem(Guid Id, Guid RootId, string RootName, string Kind, string State, string FromPath, string ToPath, string? Error, DateTimeOffset CreatedAt);
public sealed record RecoveryReceipt(Guid Id, Guid OperationId, string Actor, string Decision, string Reason, string Outcome, DateTimeOffset AppliedAt, string Kind, string RootName, string FromPath, string ToPath);
public sealed record RecoveryPage(List<RecoveryItem> Items, int Total, List<RecoveryReceipt> History);
public sealed record RecoveryObservation(StorageSnapshot Source, StorageSnapshot Destination, StorageSnapshot? Original,
    bool IsFolder, bool CanCancel, bool CanComplete, bool DestinationVerified, string Explanation, string CatalogHash);
public sealed record RecoveryInspection(Guid Id, RecoveryItem Operation, RecoveryObservation Observation, DateTimeOffset ExpiresAt);

public sealed partial class LibraryService
{
    private static IQueryable<CatalogEntry> PathMetadata(CatalogDb db, Guid rootId) => db.Entries.AsNoTracking().Where(x => x.RootId == rootId).Select(x => new CatalogEntry
    {
        Id = x.Id, RootId = x.RootId, ParentId = x.ParentId, RelativePath = x.RelativePath, Name = x.Name,
        IsFolder = x.IsFolder, IsMissing = x.IsMissing, IsIntake = x.IsIntake, TrashBatchId = x.TrashBatchId,
        Size = x.Size, ModifiedAt = x.ModifiedAt, Instructions = x.IsFolder ? x.Instructions : "",
        ContentHash = x.ContentHash, TextStatus = x.TextStatus
    });
    private static bool Unresolved(FileOperation op) => op.State is "Prepared" or "Needs review";
    private static RecoveryItem RecoveryItemFor(FileOperation op, StorageRoot root) => new(op.Id, root.Id, root.Name, op.Kind, op.State, op.FromPath, op.ToPath, op.Error, op.CreatedAt);
    private static RecoveryReceipt Receipt(OperationRecovery plan, FileOperation op, string rootName) => new(plan.Id, plan.OperationId, plan.Actor, plan.Decision, plan.Reason, plan.Outcome, plan.AppliedAt!.Value, op.Kind, rootName, op.FromPath, op.ToPath);
    private static string Digest(object data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))));
    private static StorageSnapshot? OriginalSnapshot(FileOperation op)
    {
        if (op.SourceSnapshotJson.Length == 0) return null;
        try { var snapshot = JsonSerializer.Deserialize<StorageSnapshot>(op.SourceSnapshotJson); return snapshot?.Version == 1 ? snapshot : null; }
        catch (JsonException) { return null; }
    }
    private static void RequireRecoveryActor(string actor)
    { if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200) throw new StorageException("Sign in before inspecting or resolving storage operations."); }

    public async Task<RecoveryPage> RecoveryQueueAsync(int offset = 0)
    {
        if (offset < 0 || offset > 100_000) throw new StorageException("Choose a valid recovery page.");
        await using var db = await factory.CreateDbContextAsync();
        var query = db.Operations.AsNoTracking().Where(x => x.State == "Prepared" || x.State == "Needs review");
        var roots = await db.Roots.AsNoTracking().ToDictionaryAsync(x => x.Id);
        var items = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset).Take(25).ToListAsync();
        var receipts = await db.OperationRecoveries.AsNoTracking().Where(x => x.AppliedAt != null).OrderByDescending(x => x.AppliedAt).Take(50).ToListAsync();
        var operationIds = receipts.Select(x => x.OperationId).ToArray();
        var historyOperations = await db.Operations.AsNoTracking().Where(x => operationIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        return new(items.Select(x => RecoveryItemFor(x, roots[x.RootId])).ToList(), await query.CountAsync(), receipts.Select(x => Receipt(x, historyOperations[x.OperationId], roots[historyOperations[x.OperationId].RootId].Name)).ToList());
    }

    public async Task<RecoveryInspection> InspectRecoveryAsync(Guid operationId, string actor, CancellationToken token = default)
    {
        RequireRecoveryActor(actor);
        await writer.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var op = await db.Operations.SingleOrDefaultAsync(x => x.Id == operationId, token) ?? throw new StorageException("That operation is no longer available.");
            if (!Unresolved(op)) throw new StorageException("This operation is already resolved. Refresh recovery activity.");
            var root = await db.Roots.SingleAsync(x => x.Id == op.RootId, token);
            var observation = await ObserveRecoveryAsync(db, root, op, token);
            // Prune unused inspections; applied receipts remain indefinitely.
            await db.OperationRecoveries.Where(x => x.AppliedAt == null && x.ExpiresAt < time.GetUtcNow().AddDays(-1)).ExecuteDeleteAsync(token);
            var plan = new OperationRecovery { OperationId = op.Id, Actor = actor, CreatedAt = time.GetUtcNow(), ExpiresAt = time.GetUtcNow().AddMinutes(10),
                ObservationHash = Digest(observation), ObservationJson = JsonSerializer.Serialize(observation) };
            db.OperationRecoveries.Add(plan); await db.SaveChangesAsync(token);
            return new(plan.Id, RecoveryItemFor(op, root), observation, plan.ExpiresAt);
        }
        finally { writer.Release(); }
    }

    public async Task<RecoveryReceipt> ResolveRecoveryAsync(Guid inspectionId, string decision, string reason, bool confirmed, string actor, CancellationToken token = default)
    {
        RequireRecoveryActor(actor);
        reason = reason.Trim();
        if (!confirmed) throw new StorageException("Confirm that you checked the displayed paths and understand how the catalog will change.");
        if (reason.Length is < 10 or > 1000) throw new StorageException("Record a recovery reason between 10 and 1,000 characters.");
        if (decision is not ("Cancel" or "Complete")) throw new StorageException("Choose a supported recovery decision.");
        RecoveryReceipt receipt;
        await writer.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var plan = await db.OperationRecoveries.SingleOrDefaultAsync(x => x.Id == inspectionId, token) ?? throw new StorageException("Inspect this operation before resolving it.");
            if (plan.Actor != actor) throw new StorageException("This inspection belongs to another sign-in. Inspect the operation yourself.");
            var op = await db.Operations.SingleAsync(x => x.Id == plan.OperationId, token);
            var root = await db.Roots.SingleAsync(x => x.Id == op.RootId, token);
            if (plan.AppliedAt != null)
            {
                if (plan.Decision != decision || plan.Reason != reason) throw new StorageException("This inspection already has a different recorded decision.");
                return Receipt(plan, op, root.Name);
            }
            if (plan.ExpiresAt <= time.GetUtcNow()) throw new StorageException("This inspection expired. Inspect the paths again.");
            if (!Unresolved(op)) throw new StorageException("Another recovery already resolved this operation. Refresh recovery activity.");
            var observed = await ObserveRecoveryAsync(db, root, op, token);
            if (Digest(observed) != plan.ObservationHash) throw new StorageException("Storage or catalog state changed since inspection. Inspect both paths again before deciding.");
            if ((decision == "Cancel" && !observed.CanCancel) || (decision == "Complete" && !observed.CanComplete)) throw new StorageException("That resolution is unavailable for the inspected state. Restore access or preserve the files and inspect again.");
            // This resolution changes catalog/journal only. It never moves, deletes or overwrites disk contents.
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                if (decision == "Complete") await ApplyMoveAsync(db, op, invalidateText: true);
                else await CancelRecoveredOperationAsync(db, op, "Cancelled after explicit recovery review. Original catalog location retained.", sourceMissing: observed.Source.Kind == "Missing");
                plan.Decision = decision; plan.Reason = reason; plan.Outcome = op.State; plan.AppliedAt = time.GetUtcNow();
                await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            }
            // Discovery happens only after the durable decision. Another unresolved operation still pauses this root.
            await ScanRootAsync(db, root);
            receipt = Receipt(plan, op, root.Name);
        }
        finally { writer.Release(); }
        if (!deferExtraction) await DrainExtractionAsync();
        return receipt;
    }

    private async Task CancelRecoveredOperationAsync(CatalogDb db, FileOperation op, string explanation, bool sourceMissing = false)
    {
        await CancelOrganizationAsync(db, op);
        op.State = "Cancelled"; op.Error = explanation;
        await using var transaction = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
        var prefix = op.FromPath + "/"; var now = time.GetUtcNow();
        await db.Entries.Where(x => x.RootId == op.RootId && (x.Id == op.EntryId || x.RelativePath.StartsWith(prefix))).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.IsMissing, x => sourceMissing || x.IsMissing)
            .SetProperty(x => x.ContentHash, "").SetProperty(x => x.TextStatus, "Pending").SetProperty(x => x.ExtractedText, "")
            .SetProperty(x => x.ObservedAt, now).SetProperty(x => x.Readiness, "Not queued"));
        if (op.FilingPreviewId is Guid previewId)
        {
            var preview = await db.FilingPreviews.SingleAsync(x => x.Id == previewId);
            preview.State = "Cancelled"; preview.OperationId = op.Id;
            var proposal = await db.AssistantProposals.SingleOrDefaultAsync(x => x.FilingPreviewId == previewId);
            if (proposal != null)
            {
                proposal.State = "Failed"; proposal.CompletedAt = time.GetUtcNow(); proposal.Revision++;
                Audit(db, new(proposal.OwnerId, proposal.ConnectionId), "filing.recovery", "cancelled", proposal.Id);
            }
        }
        await db.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();
    }

    private async Task<RecoveryObservation> ObserveRecoveryAsync(CatalogDb db, StorageRoot root, FileOperation op, CancellationToken token = default)
    {
        var entries = await PathMetadata(db, root.Id).OrderBy(x => x.Id).ToListAsync(token);
        var entry = entries.SingleOrDefault(x => x.Id == op.EntryId);
        var preview = op.FilingPreviewId is Guid previewId ? await db.FilingPreviews.AsNoTracking().SingleOrDefaultAsync(x => x.Id == previewId, token) : null;
        var organization = op.OrganizationFilingId is Guid organizationId ? await db.OrganizationFilings.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==organizationId,token) : null;
        var catalogHash = Digest(new { organization, RootId = root.Id, root.Location, OperationId = op.Id, op.EntryId, op.Kind, op.State, op.FromPath, op.ToPath, op.TargetParentId, op.TrashBatchId, op.FilingPreviewId, op.SourceSnapshotJson,
            Preview = preview, Entries = entries.Select(x => new { x.Id, x.RootId, x.ParentId, x.RelativePath, x.IsFolder, x.IsMissing, x.IsIntake, x.TrashBatchId, x.Size, x.ModifiedAt, x.Instructions, x.ContentHash, x.TextStatus }) });
        var unknown = new StorageSnapshot("Unavailable", "", 0, 0, 0, "Reconnect the original storage and inspect again.");
        try { await RequireOnlineAsync(root); }
        catch (StorageException) { return new(unknown, unknown, OriginalSnapshot(op), entry?.IsFolder ?? false, false, false, false, "Storage identity or availability could not be verified. Missing paths on offline storage are never treated as deletion.", catalogHash); }
        var source = await StorageSnapshotter.CaptureAsync(root.Location, op.FromPath, token);
        var target = await StorageSnapshotter.CaptureAsync(root.Location, op.ToPath, token);
        try { await RequireOnlineAsync(root); }
        catch (StorageException) { return new(unknown, unknown, OriginalSnapshot(op), entry?.IsFolder ?? false, false, false, false, "Storage became unavailable during inspection. No resolution is available.", catalogHash); }
        var original = OriginalSnapshot(op);
        var valid = entry != null && entry.RelativePath == op.FromPath && entry.RelativePath.Length > 0 && op.FromPath != op.ToPath &&
            op.Kind is "Move" or "Trash" or "Restore" or "File" or "Organize" && (op.Kind != "File" || preview != null) && (op.Kind != "Organize" || organization?.State == "Applying") &&
            !op.ToPath.StartsWith(op.FromPath + "/", StringComparison.Ordinal) && !op.FromPath.StartsWith(op.ToPath + "/", StringComparison.Ordinal);
        var readable = source.Readable && target.Readable;
        var kindMatches = entry != null && target.Kind == (entry.IsFolder ? "Folder" : "File");
        var verified = original != null && original.Exists && original.Kind == target.Kind && original.Hash == target.Hash;
        if (preview != null) verified = kindMatches && !entry!.IsFolder && target.Hash == preview.ContentHash;
        var canComplete = valid && readable && kindMatches && (verified || (original == null && op.SourceSnapshotJson.Length == 0 && op.FilingPreviewId == null));
        string? blocker = null;
        if (canComplete) blocker = await CompletionBlockerAsync(root, op, entries, token);
        canComplete &= blocker == null;
        var message = !valid ? "The catalog no longer matches the journal. Preserve both paths and repair the catalog from a verified backup before resolving this record." :
            !readable ? "Both locations must be fully readable, or confirmed missing on connected storage, before a decision is available." :
            blocker ?? (verified ? "The destination matches the original content fingerprint. Completing recovery assigns the original IDs and guidance to it. Any source copy remains untouched." :
            original == null && op.FilingPreviewId == null && op.SourceSnapshotJson.Length == 0 ? "This older operation has no original fingerprint. Destination identity cannot be proven. Adopt it only after checking the files yourself; both copies remain untouched." :
            "The destination is absent, differs from the original, or cannot be verified. Completion is blocked. Cancel to retain the original catalog location; preserve or restore files before a new move.");
        return new(source, target, original, entry?.IsFolder ?? false, valid && readable, canComplete, verified, message, catalogHash);
    }

    private static async Task<string?> CompletionBlockerAsync(StorageRoot root, FileOperation op, List<CatalogEntry> entries, CancellationToken token)
    {
        if (op.Kind == "Trash")
        {
            if (op.TrashBatchId != op.Id || op.ToPath != StoragePaths.Join(StoragePaths.Trash, op.Id.ToString()) + "/" + op.FromPath.Split('/').Last()) return "The trash path does not match the original journal. Completion is blocked.";
        }
        else
        {
            var parent = entries.SingleOrDefault(x => x.Id == op.TargetParentId);
            if (parent == null || !parent.IsFolder || parent.IsMissing || parent.TrashBatchId != null ||
                StoragePaths.Join(parent.RelativePath, op.ToPath.Split('/').Last()) != op.ToPath) return "The destination parent changed or is unavailable in the catalog. Completion is blocked.";
            var path = StoragePaths.Resolve(root.Location, parent.RelativePath);
            if (!Directory.Exists(path)) return "The destination parent is unavailable on disk. Completion is blocked.";
            if (HasIntakeAncestor(parent, entries.Where(x => x.IsFolder)) && entries.Any(x => x.IsIntake && (x.Id == op.EntryId || x.RelativePath.StartsWith(op.FromPath + "/", StringComparison.Ordinal))))
                return "Completing this move would nest intake folders. Completion is blocked.";
        }
        var moving = entries.Where(x => x.Id == op.EntryId || x.RelativePath.StartsWith(op.FromPath + "/", StringComparison.Ordinal)).ToList();
        var movingIds = moving.Select(x => x.Id).ToHashSet();
        var heldPaths = entries.Where(x => !movingIds.Contains(x.Id)).Select(x => x.RelativePath).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in moving)
        {
            token.ThrowIfCancellationRequested();
            if (heldPaths.Contains(op.ToPath + item.RelativePath[op.FromPath.Length..])) return "Another catalog item holds a destination path. Completion is blocked to preserve both identities.";
        }
        await Task.CompletedTask;
        return null;
    }
}
