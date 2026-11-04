using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

public sealed record LibraryOverview(List<StorageRoot> Roots, List<CatalogEntry> Folders, List<FileOperation> Activity, int Documents, int NeedsText, int IntakeDocuments = 0, int RecoveryCount = 0, List<Guid>? BlockedRootIds = null);
public sealed record EntryPage(List<CatalogEntry> Items, int Total);

// One writer owns filesystem changes and scans. UI and future MCP tools share this service.
// Run one application instance per catalog; network files must not have independent writers.
public sealed partial class LibraryService(IDbContextFactory<CatalogDb> factory, DocumentTextReader textReader, TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    public const long MaxUploadBytes = 100 * 1024 * 1024;

    public async Task<LibraryOverview> OverviewAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var intake = await IntakeQueryAsync(db);
        var unresolved = await db.Operations.Where(x => x.State == "Prepared" || x.State == "Needs review").Select(x => x.RootId).ToListAsync();
        return new(await db.Roots.AsNoTracking().OrderBy(x => x.Name).ToListAsync(),
            await db.Entries.AsNoTracking().Where(x => x.IsFolder && !x.IsMissing && x.TrashBatchId == null).OrderBy(x => x.Name).ToListAsync(),
            await db.Operations.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(),
            await db.Entries.CountAsync(x => !x.IsFolder && !x.IsMissing && x.TrashBatchId == null),
            await db.Entries.CountAsync(x => !x.IsFolder && !x.IsMissing && x.TrashBatchId == null && x.TextStatus != "Indexed"),
            await intake.CountAsync(), unresolved.Count, unresolved.Distinct().ToList());
    }

    public async Task<EntryPage> BrowseAsync(Guid? folderId, string search = "", int offset = 0)
    {
        await using var db = await factory.CreateDbContextAsync();
        var query = db.Entries.AsNoTracking().Where(x => !x.IsMissing && x.TrashBatchId == null && x.RelativePath != "");
        if (string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.ParentId == folderId);
        else
        {
            foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(10))
            {
                var pattern = "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                query = query.Where(x => EF.Functions.ILike(x.Name, pattern, "\\") ||
                    EF.Functions.ILike(x.RelativePath, pattern, "\\") || EF.Functions.ILike(x.ExtractedText, pattern, "\\"));
            }
        }
        return new(await query.OrderByDescending(x => x.IsFolder).ThenBy(x => x.Name).Skip(Math.Max(0, offset)).Take(100).ToListAsync(), await query.CountAsync());
    }

    public async Task<CatalogEntry?> EntryAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<(FileStream Stream, string Name)> OpenDocumentAsync(Guid id)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == id);
            if (entry == null || entry.IsFolder || entry.IsMissing || entry.TrashBatchId != null)
                throw new StorageException("That document is not available in the library.");
            var root = await db.Roots.SingleAsync(x => x.Id == entry.RootId);
            await RequireOnlineAsync(root);
            return (new FileStream(StoragePaths.Resolve(root.Location, entry.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read), entry.Name);
        }
        finally { writer.Release(); }
    }

    public async Task<List<CatalogEntry>> TrashAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await (from entry in db.Entries.AsNoTracking()
                      join op in db.Operations on entry.TrashBatchId equals op.Id
                      where op.Kind == "Trash" && op.EntryId == entry.Id
                      orderby op.CreatedAt descending select entry).ToListAsync();
    }

    public async Task<Guid> AddRootAsync(string name, string location, bool isNetwork)
    {
        await writer.WaitAsync();
        try
        {
            name = StoragePaths.ValidateName(name);
            if (!Path.IsPathFullyQualified(location)) throw new StorageException("Enter the full server path to an existing directory.");
            location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            if (!location.StartsWith(@"\\", StringComparison.Ordinal) && location.Equals(Path.GetPathRoot(location), StoragePaths.Comparison))
                throw new StorageException("Choose a dedicated documents directory, rather than an entire drive.");
            StoragePaths.Resolve(location, "");
            if (!Directory.Exists(location)) throw new StorageException("That directory is unavailable. Create or mount it on the server first.");
            await using var db = await factory.CreateDbContextAsync();
            foreach (var existing in await db.Roots.ToListAsync())
                if (StoragePaths.IsInside(location, existing.Location) || StoragePaths.IsInside(existing.Location, location))
                    throw new StorageException("Storage locations cannot overlap. Use a separate directory.");
            var root = new StorageRoot { Name = name, Location = location, IsNetwork = isNetwork };
            var marker = StoragePaths.Resolve(location, StoragePaths.Marker);
            if (File.Exists(marker))
            {
                if (!Guid.TryParse(await File.ReadAllTextAsync(marker), out var identity))
                    throw new StorageException("The storage identity file is invalid. Restore it before registering this location.");
                root.Id = identity;
                if (await db.Roots.AnyAsync(x => x.Id == identity)) throw new StorageException("This storage location is already registered.");
            }
            else
            {
                await using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var output = new StreamWriter(stream);
                await output.WriteAsync(root.Id.ToString());
            }
            db.Roots.Add(root);
            db.Entries.Add(new() { RootId = root.Id, Name = name, IsFolder = true, ModifiedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await ScanRootAsync(db, root);
            return root.Id;
        }
        finally { writer.Release(); }
    }

    public async Task ScanAsync()
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await RecoverAsync(db);
            foreach (var root in await db.Roots.ToListAsync()) await ScanRootAsync(db, root);
        }
        finally { writer.Release(); }
    }

    private static async Task RequireOnlineAsync(StorageRoot root)
    {
        try
        {
            var marker = StoragePaths.Resolve(root.Location, StoragePaths.Marker);
            if (!Directory.Exists(root.Location) || !File.Exists(marker) || (await File.ReadAllTextAsync(marker)).Trim() != root.Id.ToString())
                throw new StorageException("Storage is offline or its identity file is missing. Reconnect the original directory; indexed documents have been kept.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new StorageException("Storage cannot be reached with the server's permissions. Indexed documents have been kept."); }
    }

    private async Task ScanRootAsync(CatalogDb db, StorageRoot root)
    {
        // A previous offline root may have cleared the tracker to discard a partial scan.
        if (db.Entry(root).State == EntityState.Detached) db.Attach(root);
        try
        {
            await RequireOnlineAsync(root);
            if (await db.Operations.AnyAsync(x => x.RootId == root.Id && (x.State == "Prepared" || x.State == "Needs review"))) return;
            // Complete discovery before marking anything missing. A failed NAS traversal is not deletion.
            var found = new List<(string Relative, bool Folder, long Size, DateTimeOffset Modified)>();
            var provisioning = await db.FolderPlanSteps.Where(x => x.RootId == root.Id && x.State == "Creating").Select(x => x.Path).ToListAsync();
            void Visit(string relative)
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(StoragePaths.Resolve(root.Location, relative)).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(path);
                    if (name.StartsWith(".documentstorage", StringComparison.OrdinalIgnoreCase)) continue;
                    var attrs = File.GetAttributes(path);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    var child = StoragePaths.Join(relative, name);
                    if (provisioning.Any(x => child == x || child.StartsWith(x + "/", StringComparison.Ordinal))) continue;
                    var directory = (attrs & FileAttributes.Directory) != 0;
                    found.Add((child, directory, directory ? 0 : new FileInfo(path).Length, FileTimestamp(File.GetLastWriteTimeUtc(path))));
                    if (directory) Visit(child);
                }
            }
            Visit("");
            var entries = await db.Entries.Where(x => x.RootId == root.Id && x.TrashBatchId == null).ToListAsync();
            var byPath = entries.ToDictionary(x => x.RelativePath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var foldersById = entries.Where(x => x.IsFolder).ToDictionary(x => x.Id);
            var seen = new HashSet<string>(byPath.Comparer) { "" };
            foreach (var file in found)
            {
                seen.Add(file.Relative);
                if (!byPath.TryGetValue(file.Relative, out var entry))
                {
                    entry = new CatalogEntry { RootId = root.Id, RelativePath = file.Relative, Name = file.Relative.Split('/').Last(), IsFolder = file.Folder };
                    db.Entries.Add(entry);
                    byPath.Add(file.Relative, entry);
                }
                var parentPath = file.Relative.Contains('/') ? file.Relative[..file.Relative.LastIndexOf('/')] : "";
                entry.ParentId = byPath[parentPath].Id;
                entry.IsFolder = file.Folder;
                if (file.Folder) foldersById[entry.Id] = entry; else foldersById.Remove(entry.Id);
                var metadataChanged = entry.ModifiedAt != file.Modified || entry.Size != file.Size;
                if (metadataChanged) { entry.ObservedAt = time.GetUtcNow(); entry.ContentHash = ""; }
                var inIntake = !file.Folder && HasIntakeAncestor(entry, foldersById);
                var previousHash = entry.ContentHash;
                entry.Size = file.Size;
                entry.ModifiedAt = file.Modified;
                if (inIntake)
                {
                    if (file.Size == 0) entry.Readiness = "Empty file";
                    else if (time.GetUtcNow() - entry.ObservedAt < StabilityDelay) entry.Readiness = "Finishing scan";
                    else
                    {
                        try { entry.ContentHash = await FingerprintAsync(root, entry); entry.Readiness = "Ready"; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException) { entry.Readiness = "Waiting for access"; }
                    }
                }
                else entry.Readiness = "Not queued";
                if (!file.Folder && (!inIntake || entry.Readiness == "Ready") &&
                    (metadataChanged || previousHash != entry.ContentHash || entry.TextStatus is "Pending" or "PDF text tool unavailable"))
                {
                    TextResult extracted;
                    try { extracted = await textReader.ReadAsync(StoragePaths.Resolve(root.Location, file.Relative)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { extracted = new("", "Text unavailable"); }
                    entry.ExtractedText = extracted.Text;
                    entry.TextStatus = extracted.Status;
                    entry.PassageRevision = "";
                }
                entry.IsMissing = false;
            }
            foreach (var entry in entries) entry.IsMissing = !seen.Contains(entry.RelativePath);
            root.IsOnline = true;
            root.LastError = null;
            root.LastScan = time.GetUtcNow();
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException)
        {
            // Discard any partial catalog mutations before recording availability.
            db.ChangeTracker.Clear();
            var current = await db.Roots.SingleAsync(x => x.Id == root.Id);
            current.IsOnline = false;
            current.LastError = ex is StorageException ? ex.Message : "Storage could not be scanned. Check its connection and server permissions.";
            await db.SaveChangesAsync();
        }
    }

    private static async Task<(CatalogEntry Entry, StorageRoot Root)> WritableEntryAsync(CatalogDb db, Guid id)
    {
        var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == id) ?? throw new StorageException("That item is no longer in the catalog. Refresh and try again.");
        var root = await db.Roots.SingleAsync(x => x.Id == entry.RootId);
        await RequireOnlineAsync(root);
        if (await db.Operations.AnyAsync(x => x.RootId == root.Id && (x.State == "Prepared" || x.State == "Needs review")))
            throw new StorageException("This location has an unfinished file operation. Open Recovery to inspect and resolve it before changing files.");
        if (entry.IsMissing) throw new StorageException("That item is missing from storage. Rescan to update the catalog.");
        return (entry, root);
    }

    public async Task<Guid> CreateFolderAsync(Guid parentId, string name, string? expectedParentPath = null)
    {
        name = StoragePaths.ValidateName(name);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var (parent, root) = await WritableEntryAsync(db, parentId);
            if (!parent.IsFolder || parent.TrashBatchId != null) throw new StorageException("Choose an available destination folder.");
            if (expectedParentPath != null && parent.RelativePath != expectedParentPath) throw new StorageException("The parent folder changed since you opened the form. Refresh folders and try again.");
            if (!Directory.Exists(StoragePaths.Resolve(root.Location, parent.RelativePath))) throw new StorageException("The parent folder is unavailable on storage. Rescan before creating folders.");
            var relative = StoragePaths.Join(parent.RelativePath, name);
            var path = StoragePaths.Resolve(root.Location, relative);
            if (StoragePaths.Exists(path)) throw new StorageException("An item with that name already exists.");
            Directory.CreateDirectory(path);
            var entry = new CatalogEntry { RootId = root.Id, ParentId = parentId, Name = name, RelativePath = relative, IsFolder = true, ModifiedAt = DateTimeOffset.UtcNow };
            var stale = await db.Entries.SingleOrDefaultAsync(x => x.RootId == root.Id && x.RelativePath == relative);
            if (stale != null) { stale.IsMissing = false; stale.IsFolder = true; entry = stale; }
            else db.Entries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        }
        finally { writer.Release(); }
    }

    public Task UploadAsync(Guid parentId, string name, Stream input) => UploadToFolderAsync(parentId, name, input, false);
    public Task UploadToIntakeAsync(Guid parentId, string name, Stream input) => UploadToFolderAsync(parentId, name, input, true);

    private async Task UploadToFolderAsync(Guid parentId, string name, Stream input, bool requireIntake)
    {
        name = StoragePaths.ValidateName(name);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var (parent, root) = await WritableEntryAsync(db, parentId);
            if (!parent.IsFolder || parent.TrashBatchId != null) throw new StorageException("Choose an available destination folder.");
            if (requireIntake && !parent.IsIntake) throw new StorageException("This folder is no longer an intake destination. Choose an intake folder and try again.");
            var target = StoragePaths.Resolve(root.Location, StoragePaths.Join(parent.RelativePath, name));
            if (StoragePaths.Exists(target)) throw new StorageException("An item with that name already exists. Existing documents are never overwritten.");
            var staging = StoragePaths.Resolve(root.Location, StoragePaths.Staging);
            Directory.CreateDirectory(staging);
            var temporary = StoragePaths.Resolve(root.Location, StoragePaths.Join(StoragePaths.Staging, Guid.NewGuid().ToString()));
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer)) != 0)
                    {
                        total += count;
                        if (total > MaxUploadBytes) throw new StorageException("Each upload must be 100 MB or smaller.");
                        await output.WriteAsync(buffer.AsMemory(0, count));
                    }
                    await output.FlushAsync();
                }
                File.Move(temporary, target, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            await ScanRootAsync(db, root);
        }
        finally { writer.Release(); }
    }

    public async Task SaveInstructionsAsync(Guid folderId, string instructions, string actor = "workspace")
    {
        if (instructions.Length > 20000) throw new StorageException("Keep folder instructions under 20,000 characters.");
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var entry = await db.Entries.SingleAsync(x => x.Id == folderId);
            if (!entry.IsFolder || entry.TrashBatchId != null || entry.IsMissing) throw new StorageException("Choose an available folder.");
            entry.Instructions = instructions.Trim();
            await RecordInstructionAsync(db, new("folder", folderId.ToString()), entry.Instructions, actor);
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task MoveAsync(Guid id, Guid parentId, string name, string? expectedSourcePath = null, string? expectedDestinationPath = null) => await ChangePathAsync(id, parentId, StoragePaths.ValidateName(name), false, expectedSourcePath, expectedDestinationPath);
    public async Task TrashEntryAsync(Guid id) => await ChangePathAsync(id, null, "", true);

    private async Task ChangePathAsync(Guid id, Guid? parentId, string name, bool trash, string? expectedSourcePath = null, string? expectedDestinationPath = null)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var (entry, root) = await WritableEntryAsync(db, id);
            if (entry.RelativePath == "" || entry.TrashBatchId != null) throw new StorageException("Storage roots and items already in trash cannot be moved.");
            if (expectedSourcePath != null && entry.RelativePath != expectedSourcePath) throw new StorageException("This folder changed since you opened the form. Refresh folders and try again.");
            var op = new FileOperation { RootId = root.Id, EntryId = id, FromPath = entry.RelativePath, Kind = trash ? "Trash" : "Move", TargetParentId = parentId };
            if (trash)
            {
                op.TrashBatchId = op.Id;
                op.ToPath = StoragePaths.Join(StoragePaths.Trash, op.Id.ToString()) + "/" + entry.Name;
                Directory.CreateDirectory(StoragePaths.Resolve(root.Location, StoragePaths.Join(StoragePaths.Trash, op.Id.ToString())));
            }
            else
            {
                var (parent, parentRoot) = await WritableEntryAsync(db, parentId!.Value);
                if (!parent.IsFolder || parent.TrashBatchId != null) throw new StorageException("Choose an available destination folder.");
                if (root.Id != parentRoot.Id) throw new StorageException("Moves between storage locations are not available yet. Choose a folder in the same location.");
                if (parent.RelativePath == entry.RelativePath || parent.RelativePath.StartsWith(entry.RelativePath + "/", StringComparison.Ordinal))
                    throw new StorageException("A folder cannot be moved inside itself.");
                if (expectedDestinationPath != null && parent.RelativePath != expectedDestinationPath) throw new StorageException("The destination changed since you opened the form. Refresh folders and try again.");
                if (entry.IsFolder)
                {
                    var folders = await db.Entries.Where(x => x.RootId == root.Id && x.IsFolder && !x.IsMissing && x.TrashBatchId == null).ToListAsync();
                    if (HasIntakeAncestor(parent, folders) && folders.Any(x => x.IsIntake && (x.Id == entry.Id || x.RelativePath.StartsWith(entry.RelativePath + "/", StringComparison.Ordinal))))
                        throw new StorageException("This move would nest intake folders. Choose a destination outside intake.");
                }
                op.ToPath = StoragePaths.Join(parent.RelativePath, name);
            }
            await ExecuteMoveAsync(db, root, entry, op);
        }
        finally { writer.Release(); }
    }

    public async Task RestoreAsync(Guid id)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var (entry, root) = await WritableEntryAsync(db, id);
            var old = await db.Operations.SingleOrDefaultAsync(x => x.Id == entry.TrashBatchId && x.EntryId == id && x.Kind == "Trash")
                ?? throw new StorageException("Choose the top-level item in trash to restore it.");
            var parentPath = old.FromPath.Contains('/') ? old.FromPath[..old.FromPath.LastIndexOf('/')] : "";
            var parent = await db.Entries.SingleOrDefaultAsync(x => x.RootId == root.Id && x.RelativePath == parentPath && x.TrashBatchId == null && !x.IsMissing)
                ?? throw new StorageException("The original parent folder is unavailable. Restore its original location first.");
            var op = new FileOperation { RootId = root.Id, EntryId = id, Kind = "Restore", FromPath = entry.RelativePath, ToPath = old.FromPath, TargetParentId = parent.Id };
            await ExecuteMoveAsync(db, root, entry, op);
        }
        finally { writer.Release(); }
    }

    private async Task ExecuteMoveAsync(CatalogDb db, StorageRoot root, CatalogEntry entry, FileOperation op)
    {
        var source = StoragePaths.Resolve(root.Location, op.FromPath);
        var target = StoragePaths.Resolve(root.Location, op.ToPath);
        if (op.FromPath == op.ToPath) return;
        if (!StoragePaths.Exists(source)) throw new StorageException("The source item is unavailable. Rescan before trying again.");
        if (StoragePaths.Exists(target) || await db.Entries.AnyAsync(x => x.RootId == root.Id && x.RelativePath == op.ToPath && x.Id != entry.Id && !x.IsMissing))
            throw new StorageException("An item with that name already exists. Nothing was overwritten.");
        // Missing catalog records can hold a historical path. Preserve their identity by clearing them only after a rescan, not by overwriting.
        if (await db.Entries.AnyAsync(x => x.RootId == root.Id && x.RelativePath == op.ToPath && x.Id != entry.Id))
            throw new StorageException("That path is held by a missing catalog item. Choose another name.");
        var catalog = await PathMetadata(db, root.Id).ToListAsync();
        var moving = catalog.Where(x => x.Id == entry.Id || x.RelativePath.StartsWith(op.FromPath + "/", StringComparison.Ordinal)).ToList();
        var movingIds = moving.Select(x => x.Id).ToHashSet();
        var heldPaths = catalog.Where(x => !movingIds.Contains(x.Id)).Select(x => x.RelativePath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var child in moving)
        {
            var futurePath = op.ToPath + child.RelativePath[op.FromPath.Length..];
            if (heldPaths.Contains(futurePath))
                throw new StorageException("A descendant destination is held by another catalog item. Choose another name.");
        }
        var original = await StorageSnapshotter.CaptureAsync(root.Location, op.FromPath);
        if (!original.Exists || original.Kind != (entry.IsFolder ? "Folder" : "File")) throw new StorageException(original.Error ?? "The source changed or is unavailable. Rescan before moving it.");
        if (op.FilingPreviewId is Guid filingId && original.Hash != (await db.FilingPreviews.SingleAsync(x => x.Id == filingId)).ContentHash)
            throw new StorageException("The source contents changed after filing approval. Review the document again.");
        if (op.OrganizationFilingId is Guid organizationId && original.Hash != (await db.OrganizationFilings.SingleAsync(x => x.Id == organizationId)).ContentHash)
            throw new StorageException("The source contents changed after filing approval. Read the document again.");
        op.SourceSnapshotJson = System.Text.Json.JsonSerializer.Serialize(original);
        await RequireOnlineAsync(root);
        db.Operations.Add(op);
        await db.SaveChangesAsync();
        try
        {
            await EnsureOrganizationFolderAsync(db, root, op);
            if (entry.IsFolder) Directory.Move(source, target); else File.Move(source, target, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException)
        {
            op.State = "Needs review";
            op.Error = "The filesystem refused or could not confirm the move. Storage writes are paused; inspect it in Recovery.";
            await db.SaveChangesAsync();
            throw new StorageException(op.Error);
        }
        // Verify the complete tree, not just existence. If this save fails, Prepared survives.
        var moved = await StorageSnapshotter.CaptureAsync(root.Location, op.ToPath);
        if (moved.Kind != original.Kind || moved.Hash != original.Hash || StoragePaths.Exists(StoragePaths.Resolve(root.Location, op.FromPath)))
        {
            op.State = "Needs review"; op.Error = "Moved contents could not be verified. Storage writes are paused; preserve both paths and inspect Recovery.";
            await db.SaveChangesAsync(); throw new StorageException(op.Error);
        }
        await RequireOnlineAsync(root);
        await ApplyMoveAsync(db, op);
    }

    private async Task ApplyMoveAsync(CatalogDb db, FileOperation op, bool invalidateText = false)
    {
        await using var transaction = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
        var prefix = op.FromPath + "/";
        var now = time.GetUtcNow();
        // PostgreSQL substring counts Unicode scalars; .NET string.Length counts UTF-16 units.
        var sourceCharacters = 0;
        foreach (var rune in op.FromPath.EnumerateRunes()) sourceCharacters++;
        var trashBatch = op.Kind == "Trash" ? op.TrashBatchId : null;
        // The catalog may contain megabytes of OCR per document. Update paths in SQL,
        // retaining every ID, without materializing document contents or thousands of tracked rows.
        await db.Entries.Where(x => x.RootId == op.RootId && (x.Id == op.EntryId || x.RelativePath.StartsWith(prefix)))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.RelativePath, x => op.ToPath + x.RelativePath.Substring(sourceCharacters))
                .SetProperty(x => x.TrashBatchId, trashBatch)
                .SetProperty(x => x.ContentHash, x => invalidateText && !x.IsFolder ? "" : x.ContentHash)
                .SetProperty(x => x.TextStatus, x => invalidateText && !x.IsFolder ? "Pending" : x.TextStatus)
                .SetProperty(x => x.PassageRevision, "")
                .SetProperty(x => x.ExtractedText, x => invalidateText && !x.IsFolder ? "" : x.ExtractedText)
                .SetProperty(x => x.ObservedAt, x => invalidateText && !x.IsFolder ? now : x.ObservedAt)
                .SetProperty(x => x.Readiness, x => invalidateText && !x.IsFolder ? "Not queued" : x.Readiness));
        await db.Entries.Where(x => x.Id == op.EntryId).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.IsMissing, false).SetProperty(x => x.Name, op.ToPath.Split('/').Last())
            .SetProperty(x => x.ParentId, op.TargetParentId));
        await CompleteOrganizationAsync(db, op);
        op.State = "Completed";
        op.Error = null;
        if (op.FilingPreviewId is Guid previewId)
        {
            var preview = await db.FilingPreviews.SingleAsync(x => x.Id == previewId);
            preview.State = "Applied";
            preview.OperationId = op.Id;
            preview.AppliedAt = time.GetUtcNow();
            var approval = await db.AssistantProposals.SingleOrDefaultAsync(x => x.FilingPreviewId == previewId);
            if (approval != null)
            {
                approval.State = "Executed"; approval.CompletedAt = time.GetUtcNow(); approval.Revision++;
                approval.ResultJson = System.Text.Json.JsonSerializer.Serialize(new { operation_id = op.Id });
                Audit(db, new(approval.OwnerId, approval.ConnectionId), "filing.execute", "completed", approval.Id);
            }
        }
        await db.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();
    }

    private async Task RecoverAsync(CatalogDb db)
    {
        foreach (var op in await db.Operations.Where(x => x.State == "Prepared").OrderBy(x => x.CreatedAt).ToListAsync())
        {
            var root = await db.Roots.SingleAsync(x => x.Id == op.RootId);
            var observation = await ObserveRecoveryAsync(db, root, op);
            // An unavailable share must remain pending. It is never evidence of missing bytes.
            if (!observation.Source.Readable || !observation.Destination.Readable) continue;
            var sourceMatches = observation.Source.Kind == (observation.IsFolder ? "Folder" : "File") &&
                (observation.Original == null || observation.Source.Hash == observation.Original.Hash);
            var complete = !observation.Source.Exists && observation.DestinationVerified && observation.CanComplete;
            var cancel = observation.Source.Exists && !observation.Destination.Exists && sourceMatches && observation.CanCancel;
            if (complete || cancel)
            {
                var decision = complete ? "Complete" : "Cancel";
                db.OperationRecoveries.Add(new() { OperationId = op.Id, Actor = "system-recovery", CreatedAt = time.GetUtcNow(), ExpiresAt = time.GetUtcNow(), AppliedAt = time.GetUtcNow(),
                    ObservationHash = Digest(observation), ObservationJson = System.Text.Json.JsonSerializer.Serialize(observation), Decision = decision,
                    Reason = complete ? "Only the destination exists and its contents match the original fingerprint." : "Only the original location exists; the move did not finish.", Outcome = complete ? "Completed" : "Cancelled" });
                if (complete) await ApplyMoveAsync(db, op, invalidateText: true);
                else await CancelRecoveredOperationAsync(db, op, "Interrupted before moving. Original item preserved.");
            }
            else
            {
                op.State = "Needs review";
                op.Error = "Interrupted operation requires a recovery decision. Preserve both paths and inspect Recovery; storage writes remain paused.";
            }
        }
        await db.SaveChangesAsync();
    }
}
