using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

internal static class RecoveryChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool,string> check, Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock);
        var counter = 0;
        async Task<(string Path, Guid Root, CatalogEntry Parent, CatalogEntry Entry)> Setup(bool folder = false)
        {
            var path = Path.Combine(scratch, "case-" + ++counter); Directory.CreateDirectory(path);
            if (folder) { Directory.CreateDirectory(Path.Combine(path, "source/empty")); await File.WriteAllTextAsync(Path.Combine(path,"source/document.txt"), "Original synthetic bytes."); }
            else await File.WriteAllTextAsync(Path.Combine(path, "source.txt"), "Original synthetic bytes.");
            var root = await library.AddRootAsync("Recovery case " + counter, path, false);
            var parent = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
            var entry = (await library.BrowseAsync(parent.Id)).Items.Single();
            if (folder) await library.SaveInstructionsAsync(entry.Id, "Synthetic instructions remain with this identity.");
            return (path, root, parent, entry);
        }
        async Task<FileOperation> Prepare((string Path, Guid Root, CatalogEntry Parent, CatalogEntry Entry) state, bool legacy = false, string kind = "Move")
        {
            var op = new FileOperation { RootId = state.Root, EntryId = state.Entry.Id, Kind = kind, FromPath = state.Entry.RelativePath,
                ToPath = state.Entry.IsFolder ? "destination" : "destination.txt", TargetParentId = state.Parent.Id,
                SourceSnapshotJson = legacy ? "" : JsonSerializer.Serialize(await StorageSnapshotter.CaptureAsync(state.Path,state.Entry.RelativePath)) };
            await using var db = await factory.CreateDbContextAsync(); db.Operations.Add(op); await db.SaveChangesAsync(); return op;
        }
        async Task<string> State(FileOperation op) { await using var db = await factory.CreateDbContextAsync(); return (await db.Operations.AsNoTracking().SingleAsync(x=>x.Id==op.Id)).State; }
        const string reason = "Checked both locations against the synthetic originals.";
        const string actor = "recovery-owner";
        var test = await Setup(true); var operation = await Prepare(test);
        Directory.Move(Path.Combine(test.Path,"source"),Path.Combine(test.Path,"destination")); await library.ScanAsync();
        check(await State(operation)=="Completed" && (await library.EntryAsync(test.Entry.Id))!.RelativePath=="destination", "restart completes a fingerprint-verified folder move with original IDs");
        check((await library.EntryAsync(test.Entry.Id))!.Instructions.Contains("remain with") && Directory.Exists(Path.Combine(test.Path,"destination/empty")), "folder verification includes empty directories and retains saved guidance");
        var history = (await library.RecoveryQueueAsync()).History;
        check(history.Any(x=>x.OperationId==operation.Id && x.Actor=="system-recovery"), "automatic recovery leaves a durable decision receipt");

        test = await Setup(true);
        var childId = (await library.BrowseAsync(test.Entry.Id)).Items.Single(x=>!x.IsFolder).Id;
        var trashOp = new FileOperation { RootId=test.Root,EntryId=test.Entry.Id,Kind="Trash",FromPath="source", SourceSnapshotJson=JsonSerializer.Serialize(await StorageSnapshotter.CaptureAsync(test.Path,"source")) };
        trashOp.TrashBatchId=trashOp.Id; trashOp.ToPath=StoragePaths.Trash+"/"+trashOp.Id+"/source";
        Directory.CreateDirectory(Path.Combine(test.Path,StoragePaths.Trash,trashOp.Id.ToString()));
        await using(var db=await factory.CreateDbContextAsync()) { db.Operations.Add(trashOp);await db.SaveChangesAsync(); }
        Directory.Move(Path.Combine(test.Path,"source"),StoragePaths.Resolve(test.Path,trashOp.ToPath));await library.ScanAsync();
        check(await State(trashOp)=="Completed" && (await library.EntryAsync(test.Entry.Id))!.TrashBatchId==trashOp.Id, "verified interrupted trash retains the original folder as recoverable trash");
        var restoreOp=new FileOperation { RootId=test.Root,EntryId=test.Entry.Id,Kind="Restore",FromPath=trashOp.ToPath,ToPath="source",TargetParentId=test.Parent.Id,
            SourceSnapshotJson=JsonSerializer.Serialize(await StorageSnapshotter.CaptureAsync(test.Path,trashOp.ToPath)) };
        await using(var db=await factory.CreateDbContextAsync()) { db.Operations.Add(restoreOp);await db.SaveChangesAsync(); }
        Directory.Move(StoragePaths.Resolve(test.Path,trashOp.ToPath),Path.Combine(test.Path,"source"));await library.ScanAsync();
        check(await State(restoreOp)=="Completed" && (await library.EntryAsync(childId))!.TrashBatchId==null && (await library.EntryAsync(test.Entry.Id))!.Instructions.Contains("remain with"), "verified interrupted restore preserves descendant IDs and folder guidance");

        test = await Setup(true);
        childId = (await library.BrowseAsync(test.Entry.Id)).Items.Single(x=>!x.IsFolder).Id;
        await library.TrashEntryAsync(test.Entry.Id);
        var trashed = (await library.EntryAsync(test.Entry.Id))!;
        var absentRestore = new FileOperation { RootId=test.Root,EntryId=test.Entry.Id,Kind="Restore",FromPath=trashed.RelativePath,ToPath="source",TargetParentId=test.Parent.Id,
            SourceSnapshotJson=JsonSerializer.Serialize(await StorageSnapshotter.CaptureAsync(test.Path,trashed.RelativePath)) };
        await using(var db=await factory.CreateDbContextAsync()) { db.Operations.Add(absentRestore);await db.SaveChangesAsync(); }
        Directory.Delete(StoragePaths.Resolve(test.Path,trashed.RelativePath),true);await library.ScanAsync();
        var missingTrashPlan=await library.InspectRecoveryAsync(absentRestore.Id,actor);
        await library.ResolveRecoveryAsync(missingTrashPlan.Id,"Cancel",reason,true,actor);
        check((await library.EntryAsync(test.Entry.Id))!.IsMissing && (await library.EntryAsync(childId))!.IsMissing, "cancelling a missing restore marks the unavailable trash subtree without discarding its identities");

        test = await Setup(true);
        childId = (await library.BrowseAsync(test.Entry.Id)).Items.Single(x=>!x.IsFolder).Id;
        var neighbor=await library.CreateFolderAsync(test.Parent.Id,"📦 100XYfiles"); var neighborChild=await library.CreateFolderAsync(neighbor,"untouched");
        await library.MoveAsync(test.Entry.Id,test.Parent.Id,"📦 100%_files"); await library.MoveAsync(test.Entry.Id,test.Parent.Id,"Unicode moved");
        check((await library.EntryAsync(childId))!.RelativePath=="Unicode moved/document.txt" && File.Exists(Path.Combine(test.Path,"Unicode moved/document.txt")), "SQL folder moves preserve descendant paths for astral Unicode names");
        check((await library.EntryAsync(neighborChild))!.RelativePath=="📦 100XYfiles/untouched", "literal percent and underscore folder names do not select unrelated SQL paths");

        test = await Setup(); operation = await Prepare(test);
        await File.WriteAllTextAsync(Path.Combine(test.Path,"destination.txt"),"A different destination."); await library.ScanAsync();
        var plan = await library.InspectRecoveryAsync(operation.Id, actor);
        check(plan.Observation.CanCancel && !plan.Observation.CanComplete && !plan.Observation.DestinationVerified, "different destination bytes cannot be adopted for a fingerprinted operation");
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Complete",reason,true,actor), "manual recovery cannot override a fingerprint mismatch");
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,"another-owner"), "recovery decisions cannot use another actor's inspection");
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,false,actor), "recovery requires explicit confirmation of the displayed identity choice");
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Cancel","short",true,actor), "recovery requires a useful audit explanation");
        await File.WriteAllTextAsync(Path.Combine(test.Path,"destination.txt"), "B different destination.");
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,actor), "changed destination contents invalidate an inspected decision");
        plan = await library.InspectRecoveryAsync(operation.Id,actor); var receipt = await library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,actor);
        check(await State(operation)=="Cancelled" && File.Exists(Path.Combine(test.Path,"source.txt")) && File.Exists(Path.Combine(test.Path,"destination.txt")), "cancelling an ambiguous operation preserves both disk copies");
        check((await library.BrowseAsync(test.Parent.Id)).Total==2 && (await library.EntryAsync(test.Entry.Id))!.RelativePath=="source.txt", "cancel retains original identity and discovers a distinct destination copy");
        check((await library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,actor)).Id==receipt.Id, "repeated recovery returns its original receipt without another decision");
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Complete",reason,true,actor), "a completed inspection cannot be retried with a different resolution");
        check((await library.RecoveryQueueAsync()).History.Count(x=>x.Id==receipt.Id)==1, "manual decisions keep one audit receipt linked to the operation and actor");
        await library.CreateFolderAsync(test.Parent.Id,"Writes resumed"); check(true, "resolving the last operation resumes normal writes in that storage");

        test = await Setup(); operation = await Prepare(test);
        File.Copy(Path.Combine(test.Path,"source.txt"),Path.Combine(test.Path,"destination.txt")); await library.ScanAsync();
        plan = await library.InspectRecoveryAsync(operation.Id,actor);
        var secondPlan = await library.InspectRecoveryAsync(operation.Id,actor);
        check(plan.Observation.CanComplete && plan.Observation.DestinationVerified, "two identical copies still require an explicit identity decision");
        var originalDocument = test.Entry.Id;
        await library.ResolveRecoveryAsync(plan.Id,"Complete",reason,true,actor);
        check((await library.EntryAsync(originalDocument))!.RelativePath=="destination.txt" && (await library.BrowseAsync(test.Parent.Id)).Total==2, "completing recovery assigns the original identity to destination and indexes the remaining source separately");
        await refuses(()=>library.ResolveRecoveryAsync(secondPlan.Id,"Cancel",reason,true,actor), "a competing inspection cannot reverse a completed recovery");

        test = await Setup(); operation = await Prepare(test); File.Copy(Path.Combine(test.Path,"source.txt"),Path.Combine(test.Path,"destination.txt")); await library.ScanAsync();
        plan = await library.InspectRecoveryAsync(operation.Id,actor); var changed = Path.Combine(test.Path,"source.txt"); var modified = File.GetLastWriteTimeUtc(changed);
        await File.WriteAllTextAsync(changed,new string('X', (int)new FileInfo(changed).Length)); File.SetLastWriteTimeUtc(changed,modified);
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,actor), "same-size and same-timestamp edits still invalidate a recovery inspection");
        plan = await library.InspectRecoveryAsync(operation.Id,actor); clock.Advance(601);
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,actor), "expired inspections cannot authorize recovery");

        test = await Setup(); operation = await Prepare(test);
        File.Move(Path.Combine(test.Path,"source.txt"),Path.Combine(test.Path,"destination.txt")); await File.WriteAllTextAsync(Path.Combine(test.Path,"destination.txt"),"Wrong bytes."); await library.ScanAsync();
        check(await State(operation)=="Needs review" && (await library.EntryAsync(test.Entry.Id))!.RelativePath=="source.txt", "target-only existence does not prove an interrupted move completed");
        plan = await library.InspectRecoveryAsync(operation.Id,actor); await library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,actor);
        check((await library.EntryAsync(test.Entry.Id))!.IsMissing && File.Exists(Path.Combine(test.Path,"destination.txt")), "cancel with a missing source retains its unavailable identity and preserves the destination");

        test = await Setup(); operation = await Prepare(test); File.Delete(Path.Combine(test.Path,"source.txt")); await library.ScanAsync();
        plan = await library.InspectRecoveryAsync(operation.Id,actor);
        check(plan.Observation.Source.Kind=="Missing" && plan.Observation.Destination.Kind=="Missing" && plan.Observation.CanCancel, "connected storage can explicitly acknowledge two missing paths without fabricating contents");
        await library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,actor);
        check((await library.EntryAsync(test.Entry.Id))!.IsMissing, "acknowledging absent files marks the original unavailable without deleting catalog history");

        test = await Setup(); operation = await Prepare(test, true); var legacyModified = File.GetLastWriteTimeUtc(Path.Combine(test.Path,"source.txt")); File.Move(Path.Combine(test.Path,"source.txt"),Path.Combine(test.Path,"destination.txt"));
        await File.WriteAllTextAsync(Path.Combine(test.Path,"destination.txt"), new string('Z', "Original synthetic bytes.".Length)); File.SetLastWriteTimeUtc(Path.Combine(test.Path,"destination.txt"), legacyModified); await library.ScanAsync();
        check(await State(operation)=="Needs review", "legacy target-only operations never automatically adopt unverified content");
        plan = await library.InspectRecoveryAsync(operation.Id,actor);
        check(plan.Observation.CanComplete && !plan.Observation.DestinationVerified && plan.Observation.Original==null, "legacy adoption is explicitly identified as lacking fingerprint proof");
        await library.ResolveRecoveryAsync(plan.Id,"Complete",reason,true,actor);
        check((await library.EntryAsync(test.Entry.Id))!.RelativePath=="destination.txt", "explicit legacy adoption preserves identity without moving bytes");
        check((await library.EntryAsync(test.Entry.Id))!.ExtractedText.StartsWith("ZZZZ"), "adopting legacy contents refreshes OCR even when old size and timestamp match");

        test = await Setup(); operation = await Prepare(test); var marker = Path.Combine(test.Path,StoragePaths.Marker); File.Move(marker,marker+".offline");
        await library.ScanAsync(); plan = await library.InspectRecoveryAsync(operation.Id,actor);
        check(await State(operation)=="Prepared" && !plan.Observation.CanCancel && !plan.Observation.CanComplete, "offline storage leaves the journal pending and exposes no recovery decision");
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Cancel",reason,true,actor), "missing storage identity cannot be treated as a cancelled move");
        File.Move(marker+".offline",marker); await library.ScanAsync(); check(await State(operation)=="Cancelled", "reconnected original-only storage safely cancels the untouched operation");

        test = await Setup(true); var outside = Path.Combine(scratch,"outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(test.Path,"source/escape"),outside);
        await refuses(()=>library.MoveAsync(test.Entry.Id,test.Parent.Id,"unsafe"), "ordinary folder moves refuse nested symbolic links before journaling or touching disk");
        check(Directory.Exists(Path.Combine(test.Path,"source")) && !Directory.Exists(Path.Combine(test.Path,"unsafe")), "unverifiable folder contents remain in their original location");
        Directory.Delete(Path.Combine(test.Path,"source/escape"));
        Directory.CreateSymbolicLink(Path.Combine(test.Path,"source/dangling"),Path.Combine(outside,"absent"));
        check((await StorageSnapshotter.CaptureAsync(test.Path,"source/dangling/child.txt")).Kind=="Unavailable", "a missing child behind a dangling symbolic link is unsafe rather than confirmed missing");
        if (OperatingSystem.IsWindows()) Directory.Delete(Path.Combine(test.Path,"source/dangling")); else File.Delete(Path.Combine(test.Path,"source/dangling"));
        var huge = Path.Combine(test.Path,"source/large.bin"); await using (var stream = new FileStream(huge,FileMode.CreateNew)) stream.SetLength(StorageSnapshotter.MaxBytes+1);
        check((await StorageSnapshotter.CaptureAsync(test.Path,"source")).Kind=="Unavailable", "verification rejects oversized sparse files before reading their bytes"); File.Delete(huge);
        var limitDirectory = Path.Combine(test.Path,"entry limit"); Directory.CreateDirectory(limitDirectory);
        for(var i=0;i<StorageSnapshotter.MaxEntries;i++) await File.WriteAllTextAsync(Path.Combine(limitDirectory,i.ToString("D5")), "");
        var limited = await StorageSnapshotter.CaptureAsync(test.Path,"entry limit");
        check(limited.Kind=="Unavailable" && limited.Error!.Contains("10,000"), "verification bounds discovery itself before hashing oversized directory inventories");
        Directory.Delete(limitDirectory,true);

        test = await Setup(); operation = await Prepare(test); File.Copy(Path.Combine(test.Path,"source.txt"),Path.Combine(test.Path,"destination.txt")); await library.ScanAsync();
        plan = await library.InspectRecoveryAsync(operation.Id,actor);
        await using (var db = await factory.CreateDbContextAsync()) { var parent = await db.Entries.SingleAsync(x=>x.Id==test.Parent.Id); parent.Instructions="Changed catalog binding."; await db.SaveChangesAsync(); }
        await refuses(()=>library.ResolveRecoveryAsync(plan.Id,"Complete",reason,true,actor), "catalog changes invalidate an earlier disk inspection");

        // Inject a failure after the catalog update but before the decision receipt saves.
        plan = await library.InspectRecoveryAsync(operation.Id,actor);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var fault = new FailSecondSave();
            var options = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(fault).Options;
            var failing = new LibraryService(new FixtureFactory(options),new DocumentTextReader(),clock);
            var failed = false;
            try { await failing.ResolveRecoveryAsync(plan.Id,"Complete",reason,true,actor); } catch (InvalidOperationException ex) when (ex.Message=="Synthetic second-save failure") { failed=true; }
            check(failed && await State(operation)=="Needs review" && (await library.EntryAsync(test.Entry.Id))!.RelativePath=="source.txt", "a failed receipt save rolls back the catalog and journal together");
        }
        await library.ResolveRecoveryAsync(plan.Id,"Complete",reason,true,actor);
        check(await State(operation)=="Completed", "a rolled-back decision can be safely retried using its unchanged inspection");

        // A target path held by another catalog ID must never be reassigned, even if the bytes match.
        test = await Setup(); await File.WriteAllTextAsync(Path.Combine(test.Path,"destination.txt"),"Original synthetic bytes."); await library.ScanAsync();
        operation = await Prepare(test); await library.ScanAsync(); plan = await library.InspectRecoveryAsync(operation.Id,actor);
        check(!plan.Observation.CanComplete && plan.Observation.Explanation.Contains("holds a destination"), "recovery refuses a destination already owned by another catalog identity");
        // Race the real OS move after its journal has durably saved, without timing-dependent sleeps.
        test = await Setup();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var collision = new OnPrepared(() => File.WriteAllTextAsync(Path.Combine(test.Path,"raced.txt"),"Concurrent destination bytes."));
            var options = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(collision).Options;
            var racing = new LibraryService(new FixtureFactory(options),new DocumentTextReader(),clock);
            await refuses(()=>racing.MoveAsync(test.Entry.Id,test.Parent.Id,"raced.txt"), "a destination created after preparation is never overwritten by the OS move");
            check(await File.ReadAllTextAsync(Path.Combine(test.Path,"raced.txt"))=="Concurrent destination bytes." && File.Exists(Path.Combine(test.Path,"source.txt")), "a refused move preserves both the new collision and the source");
            check((await db.Operations.AsNoTracking().SingleAsync(x=>x.RootId==test.Root)).State=="Needs review", "a filesystem move error pauses writes instead of assuming no disk change");
        }
        test = await Setup();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var mutation = new OnPrepared(()=>File.WriteAllTextAsync(Path.Combine(test.Path,"source.txt"),"Changed after durable preparation."));
            var options = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(mutation).Options;
            var racing = new LibraryService(new FixtureFactory(options),new DocumentTextReader(),clock);
            await refuses(()=>racing.MoveAsync(test.Entry.Id,test.Parent.Id,"changed.txt"), "post-move verification refuses contents changed after the journal fingerprint was saved");
            check((await library.EntryAsync(test.Entry.Id))!.RelativePath=="source.txt" && await File.ReadAllTextAsync(Path.Combine(test.Path,"changed.txt"))=="Changed after durable preparation.", "unverified moved bytes are preserved while catalog identity stays at the original path pending recovery");
        }
        var overview = await library.OverviewAsync();
        check(overview.RecoveryCount>0 && overview.BlockedRootIds!.Contains(test.Root), "blocked roots and unresolved counts include operations beyond the activity history window");
    }
    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>now; public void Advance(int seconds)=>now=now.AddSeconds(seconds); }
    private sealed class FixtureFactory(DbContextOptions<CatalogDb> options) : IDbContextFactory<CatalogDb> { public CatalogDb CreateDbContext()=>new(options); }
    private sealed class OnPrepared(Func<Task> mutate) : SaveChangesInterceptor
    {
        private bool done;
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!done && eventData.Context!.ChangeTracker.Entries<FileOperation>().Any(x=>x.Entity.State=="Prepared")) { done=true; await mutate(); }
            return result;
        }
    }
    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        private int calls;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken=default)
        { if (++calls==2) throw new InvalidOperationException("Synthetic second-save failure"); return ValueTask.FromResult(result); }
    }
}
