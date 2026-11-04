using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class IntakeChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch,
        Action<bool,string> check, Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);
        var clock = new ManualClock();
        var library = new LibraryService(factory, new DocumentTextReader(), clock);
        var storage = Path.Combine(scratch, "storage"); Directory.CreateDirectory(storage);
        var rootId = await library.AddRootAsync("Filing checks", storage, false);
        var rootFolder = (await library.OverviewAsync()).Folders.Single(x => x.RootId == rootId && x.ParentId == null);
        var incoming = await library.CreateFolderAsync(rootFolder.Id, "Incoming");
        var archive = await library.CreateFolderAsync(rootFolder.Id, "Archive");
        await library.SetIntakeAsync(incoming, true);
        await refuses(() => library.SetIntakeAsync(rootFolder.Id, true), "intake uses a subfolder rather than an entire storage root");
        var nested = await library.CreateFolderAsync(incoming, "Nested");
        await refuses(() => library.SetIntakeAsync(nested, true), "overlapping intake folders refused");
        await library.UploadAsync(incoming, "first.txt", new MemoryStream(Encoding.UTF8.GetBytes("Synthetic Cedar statement. Balance 42.50.")));
        var first = (await library.BrowseAsync(incoming)).Items.Single(x => !x.IsFolder);
        check((await library.IntakeAsync()).Items.Single(x => x.Entry.Id == first.Id).Status == "Finishing scan", "new intake upload waits for a stable observation");
        Dictionary<string,string> Facts(string suffix) => new() { ["institution"] = "Cedar", ["account_suffix"] = suffix, ["statement_end"] = "2026-10-31" };
        await refuses(() => library.SaveReviewAsync(first.Id, 0, "bank_statement", Facts("1234"), true, "owner"), "growing or unsettled scans cannot be approved");
        clock.Advance(6); await library.ScanAsync();
        var ready = (await library.IntakeAsync()).Items.Single(x => x.Entry.Id == first.Id);
        check(ready.Entry.Readiness == "Ready" && ready.Entry.ContentHash.Length == 64 && ready.Entry.TextStatus == "Indexed", "settled intake files are fingerprinted and indexed");
        var review = await library.SaveReviewAsync(first.Id, 0, "bank_statement", Facts("1234"), false, "owner");
        check(!(await library.PreviewFilingAsync(first.Id, "owner")).Ready, "unreviewed facts never produce an executable plan");
        review = await library.SaveReviewAsync(first.Id, review.Revision, "bank_statement", Facts("1234"), true, "owner");
        await refuses(() => library.SaveReviewAsync(first.Id, 0, "bank_statement", Facts("other"), true, "owner"), "stale review edits cannot overwrite another tab");
        await refuses(() => library.SaveReviewAsync(first.Id, review.Revision, "bank_statement", new() { ["extension"] = "exe" }, true, "owner"), "file-derived facts cannot be replaced by supplied metadata");
        var input = new RuleInput(null, 0, "Cedar monthly statements", "bank_statement", archive, new() { ["institution"] = "Cedar" },
            "{statement_end:yyyy-MM-dd} - {institution} - {account_suffix}.{extension}", ["statement_end", "account_suffix"], false);
        var rule = await library.SaveRuleAsync(input, "owner");
        check(rule.Revision == 1 && !rule.Enabled, "new draft rules are persisted with their first version");
        check(!(await library.PreviewFilingAsync(first.Id, "owner")).Ready, "paused rules do not match live previews");
        int beforeOperations;
        await using (var db = await factory.CreateDbContextAsync()) beforeOperations = await db.Operations.CountAsync();
        var simulation = await library.SimulateRuleAsync(rule.Id);
        check(simulation.Single(x => x.EntryId == first.Id).Proposal.Ready && simulation.All(x => x.Proposal.PreviewId == null), "draft simulation produces names without executable approval tokens");
        await using (var db = await factory.CreateDbContextAsync()) check(await db.Operations.CountAsync() == beforeOperations && File.Exists(Path.Combine(storage,"Incoming/first.txt")), "rule simulation changes neither the filesystem nor operation journal");
        RuleInput Edit(FilingRuleDefinition value, bool? enabled = null) => new(value.Id, value.Revision, value.Name, value.DocumentType,
            value.DestinationFolderId, JsonSerializer.Deserialize<Dictionary<string,string>>(value.MatchJson)!, value.FilenameTemplate,
            JsonSerializer.Deserialize<string[]>(value.RequiredFactsJson)!, enabled ?? value.Enabled);
        rule = await library.SaveRuleAsync(Edit(rule, true), "owner");
        check((await library.RuleHistoryAsync(rule.Id)).Count == 2, "enabling a rule creates a retained new version");
        await refuses(() => library.SaveRuleAsync(input with { Id = rule.Id, ExpectedRevision = 1 }, "owner"), "stale rule edits cannot overwrite a newer version");
        await refuses(() => library.SaveRuleAsync(input with { FilenameTemplate = "../outside.txt" }, "owner"), "path-escaping filename templates refused before saving");
        await refuses(() => library.SaveRuleAsync(input with { DestinationFolderId = incoming }, "owner"), "rules cannot file back into intake");
        await refuses(() => library.SetIntakeAsync(archive, true), "intake designation cannot swallow an enabled rule destination");
        var proposal = await library.PreviewFilingAsync(first.Id, "owner");
        check(proposal.Ready && proposal.PreviewId != null && proposal.Filename == "2026-10-31 - Cedar - 1234.txt", "reviewed facts create the exact deterministic filename and destination");
        await refuses(() => library.ApplyFilingAsync(proposal.PreviewId!.Value, "another-owner"), "preview approval is bound to the requesting sign-in");
        var operationId = await library.ApplyFilingAsync(proposal.PreviewId!.Value, "owner");
        var destination = Path.Combine(storage, "Archive/2026-10-31 - Cedar - 1234.txt");
        check(!File.Exists(Path.Combine(storage,"Incoming/first.txt")) && (await File.ReadAllTextAsync(destination)).Contains("42.50"), "filing moves and renames the actual file while preserving its bytes");
        check((await library.EntryAsync(first.Id))!.Id == first.Id && !(await library.IntakeAsync()).Items.Any(x => x.Entry.Id == first.Id), "filed documents retain identity and leave the intake queue");
        check(await library.ApplyFilingAsync(proposal.PreviewId.Value, "owner") == operationId, "repeated apply returns the recorded operation without filing twice");
        await using (var db = await factory.CreateDbContextAsync()) check((await db.Operations.SingleAsync(x => x.Id == operationId)).FilingPreviewId == proposal.PreviewId &&
            (await db.FilingPreviews.SingleAsync(x => x.Id == proposal.PreviewId)).State == "Applied", "filing history links the completed operation to its approved preview");

        async Task<CatalogEntry> Add(string name, string suffix)
        {
            await library.UploadAsync(incoming, name, new MemoryStream(Encoding.UTF8.GetBytes("Synthetic Cedar statement. Balance 42.50.")));
            clock.Advance(6); await library.ScanAsync();
            var entry = (await library.BrowseAsync(incoming)).Items.Single(x => x.Name == name);
            await library.SaveReviewAsync(entry.Id, 0, "bank_statement", Facts(suffix), true, "owner");
            return entry;
        }
        var growingPath = Path.Combine(storage, "Incoming/growing.txt");
        await File.WriteAllBytesAsync(growingPath, []); await library.ScanAsync();
        var growingId = (await library.BrowseAsync(incoming)).Items.Single(x => x.Name == "growing.txt").Id;
        clock.Advance(6); await library.ScanAsync();
        check((await library.EntryAsync(growingId))!.Readiness == "Empty file", "an empty scanner placeholder never becomes ready just by waiting");
        await File.AppendAllTextAsync(growingPath, "First part. "); await library.ScanAsync();
        clock.Advance(4);
        await File.AppendAllTextAsync(growingPath, "Second part."); await library.ScanAsync();
        clock.Advance(4); await library.ScanAsync();
        check((await library.EntryAsync(growingId))!.Readiness == "Finishing scan", "additional scanner writes restart the stability window");
        clock.Advance(2); await library.ScanAsync();
        check((await library.EntryAsync(growingId))!.ExtractedText.Contains("Second part"), "the complete scan is indexed after its last write settles");
        using (var held = new FileStream(growingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await library.ScanAsync();
            check((await library.EntryAsync(growingId))!.Readiness == "Waiting for access", "a file still held exclusively by the scanner is not ready");
        }
        await library.ScanAsync();
        check((await library.EntryAsync(growingId))!.Readiness == "Ready", "an unlocked stable scan is picked up on retry");
        var second = await Add("second.txt", "0002");
        var expired = await library.PreviewFilingAsync(second.Id, "owner");
        clock.Advance(16 * 60);
        await refuses(() => library.ApplyFilingAsync(expired.PreviewId!.Value, "owner"), "expired previews cannot move files");
        var staleRule = await library.PreviewFilingAsync(second.Id, "owner");
        rule = await library.SaveRuleAsync(Edit(rule, false), "owner");
        await refuses(() => library.ApplyFilingAsync(staleRule.PreviewId!.Value, "owner"), "pausing a rule invalidates earlier previews");
        rule = await library.SaveRuleAsync(Edit(rule, true), "owner");
        var staleConflict = await library.PreviewFilingAsync(second.Id, "owner");
        var conflict = await library.SaveRuleAsync(input with { Name = "Another matching rule", Enabled = true }, "owner");
        await refuses(() => library.ApplyFilingAsync(staleConflict.PreviewId!.Value, "owner"), "a newly added conflicting rule invalidates an earlier preview");
        check(!(await library.PreviewFilingAsync(second.Id, "owner")).Ready, "multiple matching enabled rules stop filing");
        await library.SaveRuleAsync(Edit(conflict, false), "owner");
        var staleReview = await library.PreviewFilingAsync(second.Id, "owner");
        var secondReview = (await library.ReviewAsync(second.Id))!;
        await library.SaveReviewAsync(second.Id, secondReview.Revision, "bank_statement", Facts("0002"), true, "owner");
        await refuses(() => library.ApplyFilingAsync(staleReview.PreviewId!.Value, "owner"), "editing reviewed facts invalidates earlier previews");
        var staleContent = await library.PreviewFilingAsync(second.Id, "owner");
        var secondPath = Path.Combine(storage, "Incoming/second.txt");
        var originalTime = File.GetLastWriteTimeUtc(secondPath);
        await File.WriteAllTextAsync(secondPath, "Synthetic Cedar statement. Balance 43.50.");
        File.SetLastWriteTimeUtc(secondPath, originalTime);
        await refuses(() => library.ApplyFilingAsync(staleContent.PreviewId!.Value, "owner"), "changed bytes are caught even when size and timestamp are preserved");
        await library.ScanAsync();
        check((await library.IntakeAsync()).Items.Single(x => x.Entry.Id == second.Id).Status == "Document changed", "changed content returns an existing review to the attention queue");
        check(!(await library.PreviewFilingAsync(second.Id, "owner")).Ready, "stale facts cannot approve changed document contents");
        secondReview = (await library.ReviewAsync(second.Id))!;
        await library.SaveReviewAsync(second.Id, secondReview.Revision, "bank_statement", Facts("0002"), true, "owner");
        var colliding = await library.PreviewFilingAsync(second.Id, "owner");
        var occupied = Path.Combine(storage, colliding.DestinationPath!.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(occupied, "Existing unrelated bytes");
        await refuses(() => library.ApplyFilingAsync(colliding.PreviewId!.Value, "owner"), "a destination created after preview is never overwritten");
        check(await File.ReadAllTextAsync(occupied) == "Existing unrelated bytes" && File.Exists(secondPath), "collision preserves the existing file and intake source");
        check(!(await library.PreviewFilingAsync(second.Id, "owner")).Ready, "uncatalogued destination files also block new previews");
        File.Delete(occupied);
        var movedFolder = await library.PreviewFilingAsync(second.Id, "owner");
        await library.MoveAsync(archive, rootFolder.Id, "Records");
        await refuses(() => library.ApplyFilingAsync(movedFolder.PreviewId!.Value, "owner"), "destination folder changes invalidate previews of the old path");
        var renamedTarget = await library.PreviewFilingAsync(second.Id, "owner");
        check(renamedTarget.Ready && renamedTarget.DestinationPath!.StartsWith("Records/"), "rules follow destination folder identity after its rename");
        await library.ApplyFilingAsync(renamedTarget.PreviewId!.Value, "owner");

        var third = await Add("recover.txt", "0003");
        var recover = await library.PreviewFilingAsync(third.Id, "owner");
        var prepared = new FileOperation { RootId = rootId, EntryId = third.Id, Kind = "File", FromPath = third.RelativePath,
            ToPath = recover.DestinationPath!, TargetParentId = archive, FilingPreviewId = recover.PreviewId };
        await using (var db = await factory.CreateDbContextAsync())
        {
            var snapshot = await db.FilingPreviews.SingleAsync(x => x.Id == recover.PreviewId);
            snapshot.OperationId = prepared.Id; db.Operations.Add(prepared); await db.SaveChangesAsync();
        }
        File.Move(Path.Combine(storage, third.RelativePath), Path.Combine(storage, recover.DestinationPath!));
        await library.ScanAsync();
        check(await library.ApplyFilingAsync(recover.PreviewId!.Value, "owner") == prepared.Id, "restart recovery completes filing and makes retries idempotent");
        await using (var db = await factory.CreateDbContextAsync()) check((await db.FilingPreviews.SingleAsync(x => x.Id == recover.PreviewId)).State == "Applied", "recovery commits the approval and catalog path together");

        var fourth = await Add("offline.txt", "0004");
        var healthyPath = Path.Combine(scratch, "healthy"); Directory.CreateDirectory(healthyPath);
        var healthyId = await library.AddRootAsync("Healthy storage", healthyPath, false);
        var healthyScan = (await library.OverviewAsync()).Roots.Single(x => x.Id == healthyId).LastScan;
        clock.Advance(2);
        var count = (await library.IntakeAsync()).Total;
        Directory.Move(storage, storage + "-offline"); await library.ScanAsync();
        check((await library.IntakeAsync()).Total == count && (await library.IntakeAsync()).Items.Single(x => x.Entry.Id == fourth.Id).Status == "Storage offline", "offline intake retains queue items and marks storage unavailable");
        check((await library.OverviewAsync()).Roots.Single(x => x.Id == healthyId).LastScan > healthyScan, "an offline location does not prevent later healthy roots from updating");
        check(!(await library.PreviewFilingAsync(fourth.Id, "owner")).Ready, "offline storage cannot produce a filing plan");
        Directory.Move(storage + "-offline", storage); await library.ScanAsync();
        var fourthReview = (await library.ReviewAsync(fourth.Id))!;
        await library.SaveReviewAsync(fourth.Id, fourthReview.Revision, "bank_statement", Facts("../escape"), true, "owner");
        check(!(await library.PreviewFilingAsync(fourth.Id, "owner")).Ready && File.Exists(Path.Combine(storage, "Incoming/offline.txt")), "untrusted fact values cannot escape the destination folder");
        fourthReview = (await library.ReviewAsync(fourth.Id))!;
        var missing = Facts("0004"); missing.Remove("statement_end");
        await library.SaveReviewAsync(fourth.Id, fourthReview.Revision, "bank_statement", missing, true, "owner");
        check(!(await library.PreviewFilingAsync(fourth.Id, "owner")).Ready, "missing required facts stop live filing previews");
        var fifth = await Add("extension.txt", "0005");
        rule = await library.SaveRuleAsync(Edit(rule) with { FilenameTemplate = "{account_suffix}.pdf" }, "owner");
        check(!(await library.PreviewFilingAsync(fifth.Id, "owner")).Ready, "filing rules must preserve the original file extension");
        await library.SaveRuleAsync(Edit(rule) with { FilenameTemplate = input.FilenameTemplate }, "owner");
        var otherStorage = Path.Combine(scratch, "other-storage"); Directory.CreateDirectory(otherStorage);
        var otherId = await library.AddRootAsync("Other filing storage", otherStorage, true);
        var otherFolder = (await library.OverviewAsync()).Folders.Single(x => x.RootId == otherId && x.ParentId == null);
        await library.SaveRuleAsync(input with { Name = "Cross-location rule", DocumentType = "other_statement", DestinationFolderId = otherFolder.Id, Enabled = true }, "owner");
        var fifthReview = (await library.ReviewAsync(fifth.Id))!;
        await library.SaveReviewAsync(fifth.Id, fifthReview.Revision, "other_statement", Facts("0005"), true, "owner");
        check((await library.PreviewFilingAsync(fifth.Id, "owner")).Reason.Contains("storage locations"), "cross-location filing is explicitly blocked until verified transfers are implemented");
        await library.SetIntakeAsync(incoming, false);
        check((await library.IntakeAsync()).Total == 0, "turning off intake removes the queue scope without moving any document");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(int seconds) => current = current.AddSeconds(seconds);
    }
}
