using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

public sealed partial class LibraryService
{
    private readonly SemaphoreSlim extractionWork = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> extractionRequests = new();

    private async Task QueueExtractionAsync(CatalogDb db, CatalogEntry entry, bool force = false)
    {
        var version = textReader.Formats.VersionFor(entry.Name);
        if (textReader.Formats.Find(entry.Name)?.Definition.ExtractsText != true)
        {
            entry.TextStatus = "Filename only"; entry.ExtractedText = ""; entry.TextHash = "";
            entry.ExtractionVersion = version; entry.ExtractionArtifactJson = ""; entry.PassageRevision = "";
            return;
        }
        var revision = DocumentPreview.Revision(entry);
        if (!force && await db.ExtractionJobs.AnyAsync(x => x.EntryId == entry.Id && x.SourceRevision == revision && x.ExtractorVersion == version &&
            (entry.ContentHash == "" || x.SourceHash == entry.ContentHash) && (x.State == "Queued" || x.State == "Running" || x.State == "Retry"))) return;
        foreach (var active in await db.ExtractionJobs.Where(x => x.EntryId == entry.Id && (x.State == "Queued" || x.State == "Running" || x.State == "Retry")).ToListAsync())
        {
            active.State = "Superseded"; active.RunId = null; active.CompletedAt = time.GetUtcNow();
            if (extractionRequests.TryGetValue(active.Id, out var live)) try { live.Cancel(); } catch (ObjectDisposedException) { }
        }
        db.ExtractionJobs.Add(new() { EntryId = entry.Id, SourcePath = entry.RelativePath, SourceRevision = revision, SourceHash = entry.ContentHash,
            ExtractorVersion = version, CreatedAt = time.GetUtcNow(), NotBefore = time.GetUtcNow() });
        entry.TextStatus = "Text queued";
    }

    public async Task RecoverExtractionAsync()
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            foreach (var job in await db.ExtractionJobs.Where(x => x.State == "Running").ToListAsync())
            {
                job.RunId = null; job.State = job.Attempts < 3 ? "Retry" : "Failed"; job.NotBefore = time.GetUtcNow();
                job.Error = "The server restarted during text extraction.";
                if (job.State == "Failed") job.CompletedAt = time.GetUtcNow();
                var entry = await db.Entries.FindAsync(job.EntryId);
                if (entry != null && entry.TextStatus == "Extracting")
                {
                    entry.TextStatus = ExtractionMatches(entry, job) ? job.State == "Retry" ? "Text queued" : "Text extraction failed" : "Pending";
                    if (job.State == "Failed" && ExtractionMatches(entry, job)) entry.ExtractionVersion = job.ExtractorVersion;
                }
            }
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<List<ExtractionJob>> ExtractionJobsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ExtractionJobs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(30).ToListAsync();
    }
    public async Task ReextractDocumentAsync(Guid entryId, string actor)
    {
        Actor(actor); var cancelled = new List<Guid>();
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var (entry, root) = await WritableEntryAsync(db, entryId);
            if (entry.IsFolder) throw new StorageException("Choose a document to extract.");
            if (HasIntakeAncestor(entry, await db.Entries.AsNoTracking().Where(x => x.IsFolder).ToListAsync())) RequireStable(entry);
            entry.ContentHash = await FingerprintAsync(root, entry);
            await QueueExtractionAsync(db, entry, force: true);
            foreach (var reading in await db.IdentificationJobs.Where(x => x.EntryId == entry.Id && (x.State == "Queued" || x.State == "Running" || x.State == "Retry" || x.State == "Succeeded")).ToListAsync())
            { reading.State = "Superseded"; reading.RunId = null; reading.LeaseUntil = null; reading.CompletedAt = time.GetUtcNow(); reading.LastError = "Text extraction was requested again."; cancelled.Add(reading.Id); }
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); foreach (var id in cancelled) CancelLiveIdentification(id); }
        if (!deferExtraction) await DrainExtractionAsync();
    }
    private async Task DrainExtractionAsync()
    {
        // Used by synchronous Core callers/tests. The hosted app uses its persistent worker instead.
        while (await ProcessNextExtractionAsync()) { }
    }
    public async Task<bool> ProcessNextExtractionAsync(CancellationToken stoppingToken = default)
    {
        if (!await extractionWork.WaitAsync(0, stoppingToken)) return false;
        ExtractionJob? claimed = null; CatalogEntry? source = null; StorageRoot? root = null; CancellationTokenSource? live = null;
        try
        {
            await writer.WaitAsync(stoppingToken);
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                claimed = await db.ExtractionJobs.Where(x => (x.State == "Queued" || x.State == "Retry") && x.NotBefore <= time.GetUtcNow())
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(stoppingToken);
                if (claimed == null) return false;
                source = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == claimed.EntryId, stoppingToken);
                if (!ExtractionMatches(source, claimed))
                {
                    claimed.State = "Superseded"; claimed.CompletedAt = time.GetUtcNow();
                    if (source is { IsMissing: false, TrashBatchId: null, TextStatus: "Text queued" or "Extracting" } &&
                        !await db.ExtractionJobs.AnyAsync(x => x.EntryId == source.Id && x.Id != claimed.Id && (x.State == "Queued" || x.State == "Running" || x.State == "Retry"), stoppingToken))
                    { var current = await db.Entries.SingleAsync(x => x.Id == source.Id, stoppingToken); current.TextStatus = "Pending"; }
                    await db.SaveChangesAsync(stoppingToken); return true;
                }
                root = await db.Roots.AsNoTracking().SingleAsync(x => x.Id == source!.RootId, stoppingToken);
                if (!root.IsOnline || await db.Operations.AnyAsync(x => x.RootId == root.Id && (x.State == "Prepared" || x.State == "Needs review"), stoppingToken))
                { claimed.State = "Retry"; claimed.NotBefore = time.GetUtcNow().AddSeconds(30); claimed.Error = "Waiting for available storage."; await db.SaveChangesAsync(stoppingToken); return true; }
                claimed.State = "Running"; claimed.RunId = Guid.NewGuid(); claimed.Attempts++; claimed.Error = null;
                var tracked = await db.Entries.SingleAsync(x => x.Id == source!.Id, stoppingToken); tracked.TextStatus = "Extracting";
                live = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); live.CancelAfter(TimeSpan.FromSeconds(90));
                extractionRequests[claimed.Id] = live;
                await db.SaveChangesAsync(stoppingToken);
            }
            finally { writer.Release(); }

            TextResult? result = null; string? hash = null; string? failure = null; var unavailable = false; var changed = false; var online = false;
            try
            {
                await RequireOnlineAsync(root!);
                online = true;
                await using var stream = OpenFingerprintStream(root!, source!);
                hash = await HashStreamAsync(stream, root!, source!);
                if (claimed.SourceHash.Length > 0 && hash != claimed.SourceHash) throw new StorageException("Source bytes changed before extraction.");
                result = await textReader.ReadAsync(StoragePaths.Resolve(root!.Location, source!.RelativePath), live!.Token);
                if (result == null) throw new InvalidDataException("Format module returned no extraction.");
                live.Token.ThrowIfCancellationRequested();
                stream.Position = 0;
                if (hash != await HashStreamAsync(stream, root, source) || hash != await FingerprintAsync(root, source)) throw new StorageException("Source bytes changed during extraction.");
            }
            catch (OperationCanceledException) { failure = stoppingToken.IsCancellationRequested ? "The server stopped during extraction." : "Text extraction was interrupted or timed out."; }
            catch (StorageException) { unavailable = !online; changed = online; failure = online ? "Source bytes changed. Rescan before extracting again." : "Storage is unavailable. Waiting to retry."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = "The source is temporarily unavailable. Waiting to retry."; unavailable = true; }
            catch (Exception) { failure = "The format module could not extract this document."; }

            var cancelledReaders = new List<Guid>();
            await writer.WaitAsync(CancellationToken.None);
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                var job = await db.ExtractionJobs.SingleAsync(x => x.Id == claimed.Id);
                if (job.State != "Running" || job.RunId != claimed.RunId) return true;
                job.RunId = null;
                var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == job.EntryId);
                if (!ExtractionMatches(entry, job)) { job.State = "Superseded"; job.CompletedAt = time.GetUtcNow(); if (entry?.TextStatus == "Extracting" && !entry.IsMissing && entry.TrashBatchId == null) entry.TextStatus = "Pending"; }
                else if (failure != null)
                {
                    job.State = changed ? "Superseded" : unavailable || job.Attempts < 3 ? "Retry" : "Failed";
                    if (unavailable) job.Attempts--;
                    job.Error = failure; job.NotBefore = time.GetUtcNow().AddSeconds(30);
                    if (job.State is "Superseded" or "Failed") job.CompletedAt = time.GetUtcNow();
                    entry!.TextStatus = changed ? "Pending" : job.State == "Retry" ? "Text queued" : "Text extraction failed";
                    if (job.State == "Failed") entry.ExtractionVersion = job.ExtractorVersion;
                }
                else if (result != null)
                {
                    try
                    {
                        var currentRoot = await db.Roots.SingleAsync(x => x.Id == entry!.RootId);
                        await RequireOnlineAsync(currentRoot);
                        if (await FingerprintAsync(currentRoot, entry!) != hash) throw new StorageException("Source changed before extraction commit.");
                    }
                    catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
                    {
                        job.State = "Retry"; job.NotBefore = time.GetUtcNow().AddSeconds(30); job.Error = "Source unavailable or changed before indexing. Waiting for a rescan.";
                        entry!.TextStatus = "Pending"; await db.SaveChangesAsync(); return true;
                    }
                    if (result.Status is "PDF text tool unavailable" or "OCR tool unavailable") { job.State = "Retry"; job.Attempts--; job.NotBefore = time.GetUtcNow().AddSeconds(60); job.Error = result.Status; entry!.TextStatus = result.Status; }
                    else
                    {
                        if (entry!.ExtractedText != result.Text)
                            foreach (var reading in await db.IdentificationJobs.Where(x => x.EntryId == entry.Id && x.ContentHash == hash && (x.State == "Queued" || x.State == "Running" || x.State == "Retry" || x.State == "Succeeded")).ToListAsync())
                            { reading.State = "Superseded"; reading.RunId = null; reading.LeaseUntil = null; reading.CompletedAt = time.GetUtcNow(); reading.LastError = "Extracted text changed. Read again with AI for a fresh interpretation."; cancelledReaders.Add(reading.Id); }
                        entry.ExtractedText = result.Text; entry.TextHash = TextHash(result.Text); entry.TextStatus = result.Status;
                        entry.ContentHash = hash!; entry.ExtractionSourceHash = hash!; entry.ExtractionVersion = job.ExtractorVersion;
                        entry.ExtractionTruncated = result.Truncated; entry.ExtractionArtifactJson = result.ArtifactJson; entry.PassageRevision = "";
                        job.SourceHash = hash!; job.State = result.Status is "Indexed" or "Needs OCR" or "No text" or "No text found" or "Filename only" ? "Succeeded" : "Failed";
                        job.Error = job.State == "Failed" ? result.Status : null; job.CompletedAt = time.GetUtcNow();
                    }
                }
                await db.SaveChangesAsync();
            }
            finally { writer.Release(); foreach (var id in cancelledReaders) CancelLiveIdentification(id); }
            return true;
        }
        finally
        {
            if (claimed != null) extractionRequests.TryRemove(claimed.Id, out _);
            live?.Dispose(); extractionWork.Release();
        }
    }
    private bool ExtractionMatches(CatalogEntry? entry, ExtractionJob job) => entry != null && !entry.IsFolder && !entry.IsMissing && entry.TrashBatchId == null &&
        DocumentPreview.Revision(entry) == job.SourceRevision && textReader.Formats.VersionFor(entry.Name) == job.ExtractorVersion;
}
