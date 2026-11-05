using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

public sealed record IdentificationContext(string Json, string Hash, string[] Types, string[] Facts, bool Truncated, bool Organizing = false, bool Sorting = false, string InputText = "");
public sealed record IdentificationOverview(OllamaSettings Settings, List<DocumentTypeProfile> Types, List<IdentificationJob> Jobs);

public sealed partial class LibraryService
{
    private sealed record LiveIdentification(Guid RunId, CancellationTokenSource Cancellation);
    private readonly ConcurrentDictionary<Guid,LiveIdentification> identificationRequests = new();
    private static readonly HashSet<string> ReservedAiFacts = ["extension", "original_stem", "filename", "destination", "path", "command", "reviewed", "needs_review"];
    private static readonly string[] CommonAiFacts = ["institution", "account_suffix", "statement_start", "statement_end", "invoice_number", "invoice_date", "due_date", "total", "currency", "counterparty", "document_date", "tax_year"];

    private static DocumentTypeProfile[] DefaultTypes() =>
    [
        new() { Key = "bank_statement", Name = "Bank statement", Instructions = "A periodic account statement, not an advertisement, payment confirmation or individual transaction. Extract the statement period and account suffix, not a download date.", FactNamesJson = "[\"institution\",\"account_suffix\",\"statement_start\",\"statement_end\",\"currency\"]" },
        new() { Key = "invoice", Name = "Invoice", Instructions = "An invoice requesting payment for goods or services. Distinguish an invoice date, due date and invoice number.", FactNamesJson = "[\"counterparty\",\"invoice_number\",\"invoice_date\",\"due_date\",\"total\",\"currency\"]" },
        new() { Key = "receipt", Name = "Receipt", Instructions = "Evidence of a completed purchase or payment, rather than a request for payment.", FactNamesJson = "[\"counterparty\",\"document_date\",\"total\",\"currency\"]" },
        new() { Key = "tax_document", Name = "Tax document", Instructions = "A tax form or tax record. Distinguish its tax year from its issue or download date.", FactNamesJson = "[\"counterparty\",\"tax_year\",\"document_date\"]" },
        new() { Key = "other", Name = "Other document", Instructions = "Use only when none of the configured document types fits. Do not invent missing facts.", FactNamesJson = "[\"counterparty\",\"document_date\"]" }
    ];

    private static async Task<OllamaSettings> AiDefaultsAsync(CatalogDb db)
    {
        var settings = await db.OllamaConfigurations.SingleOrDefaultAsync(x => x.Id == 1);
        if (settings == null) { settings = new(); db.OllamaConfigurations.Add(settings); }
        if (!await db.DocumentTypes.AnyAsync()) db.DocumentTypes.AddRange(DefaultTypes());
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();
        return settings;
    }

    public async Task<IdentificationOverview> IdentificationOverviewAsync()
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var settings = await AiDefaultsAsync(db);
            return new(settings, await db.DocumentTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(),
                await db.IdentificationJobs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync());
        }
        finally { writer.Release(); }
    }

    public async Task<OllamaSettings> SaveOllamaSettingsAsync(OllamaSettings input, int expectedRevision, string actor)
    {
        Actor(actor);
        var endpoint = OllamaClient.ValidateEndpoint(input.Endpoint);
        var model = input.Model.Trim();
        if (model.Length > 128 || model.Any(char.IsControl) || (input.Enabled && model.Length == 0)) throw new StorageException("Choose a local installed model before enabling identification.");
        if (model.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase)) throw new StorageException("Choose a locally installed model; cloud-backed models are not used.");
        if (input.TimeoutSeconds is < 30 or > 1800 || input.MaxInputCharacters is < 2000 or > 16000)
            throw new StorageException("Use a timeout of 30–1800 seconds and OCR text limit of 2000–16000 characters.");
        if (input.Instructions.Length > 4000) throw new StorageException("Keep identification guidance under 4,000 characters.");
        var cancelled = new List<Guid>();
        var cancelledConversations = new List<Guid>();
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var settings = await AiDefaultsAsync(db);
            if (settings.Revision != expectedRevision) throw new StorageException("Local AI settings changed in another tab. Reload before saving.");
            settings.Endpoint = endpoint; settings.Model = model; settings.Enabled = input.Enabled;
            settings.AutoIdentify = input.Enabled && input.AutoIdentify; settings.TimeoutSeconds = input.TimeoutSeconds;
            settings.MaxInputCharacters = input.MaxInputCharacters; settings.Instructions = input.Instructions.Trim(); settings.Revision++;
            foreach (var job in await db.IdentificationJobs.Where(x => x.State == "Queued" || x.State == "Retry" || x.State == "Running").ToListAsync())
            {
                job.State = "Cancelled"; job.LastError = "Local AI settings changed. Queue again with the new settings.";
                job.CompletedAt = time.GetUtcNow(); job.RunId = null; job.LeaseUntil = null; cancelled.Add(job.Id);
            }
            foreach (var turn in await db.DeskTurns.Where(x => x.State == "Queued" || x.State == "Running").ToListAsync())
            {
                turn.State = "Cancelled"; turn.Error = "Local reader settings changed. Your message is saved; try again with the new settings.";
                cancelledConversations.Add(turn.Id);
            }
            await RecordInstructionAsync(db, new("global", "global"), settings.Instructions, actor);
            await db.SaveChangesAsync();
            return settings;
        }
        finally { writer.Release(); foreach (var id in cancelled) CancelLiveIdentification(id); foreach (var id in cancelledConversations) CancelLiveDeskMessage(id); }
    }

    public async Task<DocumentTypeProfile> SaveDocumentTypeAsync(DocumentTypeProfile input, int expectedRevision, string actor)
    {
        Actor(actor);
        var key = FactKey(input.Key);
        if (key == "unknown") throw new StorageException("Unknown is reserved for uncertain identification.");
        var name = input.Name.Trim();
        if (name.Length is 0 or > 80 || input.Instructions.Length > 2000) throw new StorageException("Use a type name up to 80 characters and guidance up to 2,000 characters.");
        string[] fields;
        try { fields = JsonSerializer.Deserialize<string[]>(input.FactNamesJson)!.Select(FactKey).Distinct(StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (ex is JsonException or NullReferenceException) { throw new StorageException("Enter a list of fact names."); }
        if (fields.Length > 32 || fields.Any(ReservedAiFacts.Contains)) throw new StorageException("Use up to 32 extraction facts. File operations and review flags are not model facts.");
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync(); await AiDefaultsAsync(db);
            var profile = await db.DocumentTypes.SingleOrDefaultAsync(x => x.Key == key);
            if ((profile?.Revision ?? 0) != expectedRevision) throw new StorageException("This document type changed in another tab. Reload it before saving.");
            if (profile == null) { profile = new() { Key = key, Revision = 0 }; db.DocumentTypes.Add(profile); }
            profile.Name = name; profile.Instructions = input.Instructions.Trim(); profile.FactNamesJson = JsonSerializer.Serialize(fields); profile.Revision++;
            await RecordInstructionAsync(db, new("document_type", profile.Key), profile.Instructions, actor);
            await db.SaveChangesAsync(); return profile;
        }
        finally { writer.Release(); }
    }

    public async Task<IdentificationJob?> LatestIdentificationAsync(Guid entryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.IdentificationJobs.AsNoTracking().Where(x => x.EntryId == entryId).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
    }

    public async Task<Guid> QueueIdentificationAsync(Guid entryId, string actor, bool retry = false, bool organizing = false, bool sorting = false)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var settings = await AiDefaultsAsync(db);
            if (!settings.Enabled) throw new StorageException("Enable a local model in Local AI before identifying documents.");
            var (entry, root) = await WritableEntryAsync(db, entryId);
            await RequireIntakeAsync(db, entry); RequireStable(entry);
            if (entry.TextStatus != "Indexed" || string.IsNullOrWhiteSpace(entry.ExtractedText)) throw new StorageException("Server text is not available (" + entry.TextStatus + "). Restore text extraction and rescan before reading with AI.");
            if (await FingerprintAsync(root, entry) != entry.ContentHash) throw new StorageException("The document changed. Rescan it before identification.");
            var context = sorting ? await SortingContextAsync(db, entry, settings) : organizing ? await OrganizationContextAsync(db, entry, settings) : await IdentificationContextAsync(db, entry, settings);
            return await EnqueueIdentificationAsync(db, entry, settings, context, actor, retry);
        }
        finally { writer.Release(); }
    }

    private async Task<Guid> EnqueueIdentificationAsync(CatalogDb db, CatalogEntry entry, OllamaSettings settings, IdentificationContext context, string actor, bool retry)
    {
        var reviewRevision = (await db.Reviews.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entry.Id))?.Revision ?? 0;
        var previous = await db.IdentificationJobs.Where(x => x.Organizing == context.Organizing && x.Sorting == context.Sorting && x.EntryId == entry.Id && x.ContentHash == entry.ContentHash && x.SourcePath == entry.RelativePath &&
            x.SettingsRevision == settings.Revision && x.ContextHash == context.Hash && x.ReviewRevision == reviewRevision).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (previous != null && (!retry || previous.State is "Queued" or "Running" or "Retry")) return previous.Id;
        var job = new IdentificationJob
        {
            Organizing = context.Organizing, Sorting = context.Sorting, EntryId = entry.Id, ContentHash = entry.ContentHash, SourcePath = entry.RelativePath, ReviewRevision = reviewRevision,
            SettingsRevision = settings.Revision, ContextHash = context.Hash, ContextJson = context.Json,
            Endpoint = settings.Endpoint, Model = settings.Model, InputTruncated = context.Truncated || (!context.Sorting && entry.ExtractedText.Length > settings.MaxInputCharacters),
            CreatedAt = time.GetUtcNow(), NotBefore = time.GetUtcNow(), CreatedBy = actor
        };
        db.IdentificationJobs.Add(job); await db.SaveChangesAsync(); return job.Id;
    }

    // A rotating batch prevents already-processed first-page documents starving later arrivals.
    public async Task<int> QueueAutomaticIdentificationAsync(int offset)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync(); var settings = await AiDefaultsAsync(db);
            if (!settings.Enabled || !settings.AutoIdentify) return 0;
            var query = (await IntakeQueryAsync(db)).Where(x => x.Readiness == "Ready" && x.TextStatus == "Indexed" && x.ExtractedText != "");
            var total = await query.CountAsync();
            if (offset >= total) offset = 0;
            var entries = await query.OrderBy(x => x.ObservedAt).ThenBy(x => x.Id).Skip(offset).Take(25).ToListAsync();
            foreach (var entry in entries)
            {
                var root = await db.Roots.AsNoTracking().SingleAsync(x => x.Id == entry.RootId);
                var review = await db.Reviews.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entry.Id);
                if (!root.IsOnline || (review?.Reviewed == true && review.ContentHash == entry.ContentHash)) continue;
                // Automatic intake is once per document version, not once per workspace context.
                // Folder/guidance/settings edits can invalidate approval without buying another
                // model reading. Keep saved results and require an explicit retry in that case.
                // Also let an existing active job settle before scheduling a changed version.
                if (await db.IdentificationJobs.AnyAsync(x => x.EntryId == entry.Id &&
                    (x.ContentHash == entry.ContentHash || x.State == "Queued" || x.State == "Running" || x.State == "Retry"))) continue;
                await EnqueueIdentificationAsync(db, entry, settings, await SortingContextAsync(db, entry, settings), "automatic", false);
            }
            return offset + entries.Count >= total ? 0 : offset + entries.Count;
        }
        finally { writer.Release(); }
    }

    public async Task CancelIdentificationAsync(Guid jobId, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var job = await db.IdentificationJobs.SingleOrDefaultAsync(x => x.Id == jobId) ?? throw new StorageException("That identification job no longer exists.");
            if (job.State is not ("Queued" or "Retry" or "Running")) return;
            job.State = "Cancelled"; job.CompletedAt = time.GetUtcNow(); job.LastError = "Cancelled by the user."; job.RunId = null; job.LeaseUntil = null;
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); CancelLiveIdentification(jobId); }
    }

    private void CancelLiveIdentification(Guid jobId)
    {
        if (identificationRequests.TryGetValue(jobId, out var request))
            try { request.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task RecoverIdentificationAsync()
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            foreach (var job in await db.IdentificationJobs.Where(x => x.State == "Running").ToListAsync())
            {
                job.State = job.Attempts < 3 ? "Retry" : "Failed"; job.NotBefore = time.GetUtcNow(); job.RunId = null; job.LeaseUntil = null;
                if (job.State == "Failed") job.CompletedAt = time.GetUtcNow();
                job.LastError = "The server restarted before identification finished. Retry is limited to three attempts.";
            }
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<bool> ProcessNextIdentificationAsync(IOllamaClient client, CancellationToken stoppingToken = default)
    {
        if (!await modelWork.WaitAsync(0, stoppingToken)) return false;
        try { return await ProcessIdentificationCoreAsync(client, stoppingToken); }
        finally { modelWork.Release(); }
    }
    private async Task<bool> ProcessIdentificationCoreAsync(IOllamaClient client, CancellationToken stoppingToken)
    {
        IdentificationRequest? input = null;
        IdentificationJob? claimed = null;
        LiveIdentification? live = null;
        SortRequest? sortInput = null;
        SortReply? sortReply = null;
        var sorted = false;
        await writer.WaitAsync(stoppingToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(stoppingToken); var settings = await AiDefaultsAsync(db);
            if (!settings.Enabled) return false;
            foreach (var stale in await db.IdentificationJobs.Where(x => x.State == "Running" && x.LeaseUntil < time.GetUtcNow()).ToListAsync(stoppingToken))
            { stale.State = stale.Attempts < 3 ? "Retry" : "Failed"; stale.RunId = null; stale.LeaseUntil = null; stale.NotBefore = time.GetUtcNow(); if (stale.State == "Failed") stale.CompletedAt = time.GetUtcNow(); stale.LastError = "The previous worker lease expired. Retry is limited to three attempts."; }
            await db.SaveChangesAsync(stoppingToken);
            if (await db.IdentificationJobs.AnyAsync(x => x.State == "Running", stoppingToken)) return false;
            claimed = await db.IdentificationJobs.Where(x => (x.State == "Queued" || x.State == "Retry") && x.NotBefore <= time.GetUtcNow())
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(stoppingToken);
            if (claimed == null) return false;
            var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == claimed.EntryId, stoppingToken);
            var review = entry == null ? null : await db.Reviews.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entry.Id, stoppingToken);
            if (entry == null || entry.IsMissing || entry.TrashBatchId != null || entry.RelativePath != claimed.SourcePath || settings.Revision != claimed.SettingsRevision ||
                (review?.Revision ?? 0) != claimed.ReviewRevision || !HasIntakeAncestor(entry, await db.Entries.AsNoTracking().Where(x => x.IsFolder).ToListAsync(stoppingToken)))
            { await SupersedeAsync(db, claimed, "Document, review or settings changed before identification."); return true; }
            if (entry.Readiness != "Ready") { claimed.State = "Retry"; claimed.NotBefore = time.GetUtcNow().AddSeconds(30); claimed.LastError = "Waiting for the scan to settle."; await db.SaveChangesAsync(stoppingToken); return true; }
            var context = await JobContextAsync(db, entry, settings, claimed);
            if (entry.ContentHash != claimed.ContentHash || context.Hash != claimed.ContextHash)
            { await SupersedeAsync(db, claimed, "Document contents or identification guidance changed. Queue a fresh job."); return true; }
            try
            {
                var (_, root) = await WritableEntryAsync(db, entry.Id);
                if (await FingerprintAsync(root, entry) != claimed.ContentHash) { await SupersedeAsync(db, claimed, "Document bytes changed. Rescan before identifying again."); return true; }
            }
            catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
            { claimed.State = "Retry"; claimed.NotBefore = time.GetUtcNow().AddSeconds(30); claimed.LastError = "Storage is unavailable. Waiting to retry."; await db.SaveChangesAsync(stoppingToken); return true; }
            if (entry.TextStatus != "Indexed" || string.IsNullOrWhiteSpace(entry.ExtractedText))
            { await SupersedeAsync(db, claimed, "OCR text is no longer available. Rescan the document."); return true; }
            var text = entry.ExtractedText;
            if (claimed.Sorting) text = context.InputText;
            else if (text.Length > settings.MaxInputCharacters)
            {
                const string marker = "\n[OCR EXCERPT OMITTED]\n";
                var budget = settings.MaxInputCharacters - marker.Length;
                var head = budget * 3 / 4;
                text = text[..head] + marker + text[^(budget - head)..];
            }
            input = new(settings.Endpoint, settings.Model, settings.TimeoutSeconds, entry.Name, context.Json, entry.ExtractedText, text,
                context.Truncated || (!claimed.Sorting && entry.ExtractedText.Length > settings.MaxInputCharacters), context.Types, context.Facts, claimed.Organizing);
            if (claimed.Sorting) sortInput = new(settings.Endpoint, settings.Model, settings.TimeoutSeconds, context.Json);
            claimed.ContextJson = context.Json;
            claimed.InputText = text;
            claimed.State = "Running"; claimed.Attempts++; claimed.RunId = Guid.NewGuid(); claimed.LeaseUntil = time.GetUtcNow().AddSeconds(settings.TimeoutSeconds + 60); claimed.LastError = null;
            live = new(claimed.RunId.Value, CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
            identificationRequests[claimed.Id] = live;
            await db.SaveChangesAsync(stoppingToken);
        }
        catch
        {
            if (claimed != null && live != null)
            {
                ((ICollection<KeyValuePair<Guid,LiveIdentification>>)identificationRequests).Remove(new(claimed.Id, live));
                live.Cancellation.Dispose();
            }
            throw;
        }
        finally { writer.Release(); }

        if (input == null || claimed == null || live == null) return false;
        IdentificationResult? result = null; string? failure = null; var transient = false;
        try
        {
            try { if (sortInput != null) sortReply = await client.SortAsync(sortInput, live.Cancellation.Token); else result = await client.IdentifyAsync(input, live.Cancellation.Token); }
            catch (OperationCanceledException) { failure = stoppingToken.IsCancellationRequested ? "Server stopping. The saved job will retry." : "Identification was cancelled."; transient = stoppingToken.IsCancellationRequested; }
            catch (OllamaException ex) { failure = ex.Message; transient = ex.Transient; }
            catch (Exception) { failure = "Identification failed unexpectedly. Check the local model connection and retry."; }
            await writer.WaitAsync(CancellationToken.None);
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                var job = await db.IdentificationJobs.SingleAsync(x => x.Id == claimed.Id);
                if (job.State != "Running" || job.RunId != live.RunId) return true; // Cancellation/recovery wins over late responses.
                job.RunId = null; job.LeaseUntil = null;
                if (failure != null)
                {
                    job.State = transient && job.Attempts < 3 ? "Retry" : "Failed";
                    job.NotBefore = time.GetUtcNow().AddSeconds(job.Attempts == 1 ? 30 : 120); job.LastError = failure;
                    if (job.State == "Failed") job.CompletedAt = time.GetUtcNow();
                    await db.SaveChangesAsync(); return true;
                }
                var settings = await db.OllamaConfigurations.SingleAsync(x => x.Id == 1);
                var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == job.EntryId);
                var review = await db.Reviews.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == job.EntryId);
                if (entry == null || entry.IsMissing || entry.TrashBatchId != null || entry.RelativePath != job.SourcePath || (review?.Revision ?? 0) != job.ReviewRevision ||
                    !settings.Enabled || settings.Revision != job.SettingsRevision || (await JobContextAsync(db, entry, settings, job)).Hash != job.ContextHash)
                { await SupersedeAsync(db, job, "The document, review or guidance changed during identification. Its response was discarded."); return true; }
                try
                {
                    await RequireIntakeAsync(db, entry); RequireStable(entry);
                    var (_, root) = await WritableEntryAsync(db, entry.Id);
                    if (await FingerprintAsync(root, entry) != job.ContentHash) { await SupersedeAsync(db, job, "Document contents changed during identification. Its response was discarded."); return true; }
                }
                catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
                { await SupersedeAsync(db, job, "The source is unavailable or changed. Rescan before identifying again."); return true; }
                if (sortInput != null)
                {
                    if (sortReply == null) { job.State = "Failed"; job.LastError = "The local model returned no reading."; job.CompletedAt = time.GetUtcNow(); }
                    else
                    {
                        job.OrganizationJson = JsonSerializer.Serialize(sortReply); job.DocumentType = sortReply.Kind; job.Summary = sortReply.Title;
                        job.FactsJson = JsonSerializer.Serialize(sortReply.Fields.ToDictionary(x => x.Name, x => x.Value));
                        job.EvidenceJson = JsonSerializer.Serialize(sortReply.Fields.ToDictionary(x => x.Name, x => x.Quote));
                        job.NeedsReview = sortReply.Confidence != "high"; job.State = "Succeeded"; job.CompletedAt = time.GetUtcNow(); sorted = true;
                    }
                    await db.SaveChangesAsync(); return true;
                }
                if (input.Organizing && result != null)
                {
                    try { result = ValidateOrganizationResult(input, result); }
                    catch (OllamaException) { result = null; }
                }
                if (result == null || (!input.Organizing && (!input.DocumentTypes.Contains(result.DocumentType) || result.Facts.Keys.Any(x => !input.FactNames.Contains(x)))))
                { job.State = "Failed"; job.LastError = "The local model returned an invalid identification result."; job.CompletedAt = time.GetUtcNow(); }
                else
                {
                    job.OrganizationJson = result.Organization == null ? "" : JsonSerializer.Serialize(result.Organization);
                    job.DocumentType = result.DocumentType; job.FactsJson = JsonSerializer.Serialize(NormalizeFacts(result.Facts));
                    job.EvidenceJson = JsonSerializer.Serialize(result.Evidence); job.Summary = result.Summary;
                    job.NeedsReview = result.NeedsReview || job.InputTruncated || result.Organization?.DestinationFolderId == Guid.Empty; job.State = "Succeeded"; job.CompletedAt = time.GetUtcNow();
                }
                await db.SaveChangesAsync();
            }
            finally { writer.Release(); }
        }
        finally
        {
            ((ICollection<KeyValuePair<Guid,LiveIdentification>>)identificationRequests).Remove(new(claimed.Id, live));
            live.Cancellation.Dispose();
            if (sorted)
                try { await TryAutoFileAsync(claimed.Id); }
                catch (Exception ex) when (ex is not OperationCanceledException) { } // The card simply waits for a person.
        }
        return true;
    }

    private async Task SupersedeAsync(CatalogDb db, IdentificationJob job, string reason)
    {
        job.State = "Superseded"; job.LastError = reason; job.CompletedAt = time.GetUtcNow(); job.RunId = null; job.LeaseUntil = null;
        await db.SaveChangesAsync();
    }

    public async Task<IdentificationJob> UsableIdentificationAsync(Guid jobId)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var job = await db.IdentificationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId) ?? throw new StorageException("Identification proposal not found.");
            if (job.State != "Succeeded") throw new StorageException("This job has no usable proposal.");
            var (entry, root) = await WritableEntryAsync(db, job.EntryId); await RequireIntakeAsync(db, entry); RequireStable(entry);
            var review = await db.Reviews.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entry.Id);
            var settings = await db.OllamaConfigurations.SingleAsync(x => x.Id == 1);
            if (!settings.Enabled || job.SourcePath != entry.RelativePath || (review?.Revision ?? 0) != job.ReviewRevision || settings.Revision != job.SettingsRevision ||
                (await JobContextAsync(db, entry, settings, job)).Hash != job.ContextHash || await FingerprintAsync(root, entry) != job.ContentHash)
                throw new StorageException("The document, review or identification guidance changed. Queue a fresh proposal.");
            return job; // This only loads facts into an editor; it never approves a review or files a document.
        }
        finally { writer.Release(); }
    }

    private static string[] ProfileFields(DocumentTypeProfile profile) => JsonSerializer.Deserialize<string[]>(profile.FactNamesJson) ?? [];
    private async Task<IdentificationContext> IdentificationContextAsync(CatalogDb db, CatalogEntry entry, OllamaSettings settings)
    {
        var profiles = await db.DocumentTypes.AsNoTracking().OrderBy(x => x.Key).ToListAsync();
        var rules = await db.FilingRules.AsNoTracking().Where(x => x.Enabled).OrderBy(x => x.Id).ToListAsync();
        var types = profiles.Select(x => x.Key).Concat(rules.Select(x => x.DocumentType)).Where(x => x != "unknown").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var truncated = types.Length > 31;
        types = types.Take(31).Append("unknown").ToArray();
        var allFields = CommonAiFacts.Concat(profiles.SelectMany(ProfileFields)).Concat(rules.SelectMany(x =>
            JsonSerializer.Deserialize<string[]>(x.RequiredFactsJson)!.Concat(JsonSerializer.Deserialize<Dictionary<string,string>>(x.MatchJson)!.Keys)))
            .Where(x => !ReservedAiFacts.Contains(x)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        truncated |= allFields.Length > 64; var fields = allFields.Take(64).ToArray();
        var folders = await db.Entries.AsNoTracking().Where(x => x.RootId == entry.RootId && x.IsFolder).ToDictionaryAsync(x => x.Id);
        var guidance = new List<string>(); var folder = folders.GetValueOrDefault(entry.ParentId ?? Guid.Empty);
        while (folder != null) { if (folder.Instructions.Length > 0) guidance.Insert(0, folder.Instructions); folder = folders.GetValueOrDefault(folder.ParentId ?? Guid.Empty); }
        string Limit(string value, int maximum) { if (value.Length <= maximum) return value; truncated = true; return value[..maximum]; }
        var global = Limit(settings.Instructions, 2000); var inherited = Limit(string.Join("\n\n", guidance), 3000);
        var hints = rules.Take(16).Select(x => new
        {
            type = x.DocumentType, revision = x.Revision,
            matching_facts = JsonSerializer.Deserialize<Dictionary<string,string>>(x.MatchJson)!.Take(4).ToDictionary(x => x.Key, x => Limit(x.Value, 100))
        }).ToArray();
        truncated |= rules.Count > 16;
        string Build(int detail, bool includeHints) => JsonSerializer.Serialize(new
        {
            schema_version = 1, guidance = global, folder_guidance = inherited, allowed_facts = fields,
            types = types.Select(key => new { key, name = detail == 0 ? key : profiles.FirstOrDefault(x => x.Key == key)?.Name ?? key,
                guidance = Limit(profiles.FirstOrDefault(x => x.Key == key)?.Instructions ?? "", detail), revision = profiles.FirstOrDefault(x => x.Key == key)?.Revision ?? 0 }),
            rule_hints = includeHints ? hints : null
        });
        var detailBudget = Math.Min(1000, 8000 / Math.Max(1, types.Length));
        var json = Build(detailBudget, true);
        if (json.Length > 16000) { truncated = true; json = Build(Math.Min(detailBudget, 300), false); }
        if (json.Length > 16000) { inherited = Limit(inherited, 1000); global = Limit(global, 1000); json = Build(0, false); }
        if (json.Length > 16000) { inherited = global = ""; json = Build(0, false); }
        // Bind even omitted guidance and rules: editing text beyond the prompt budget invalidates old work too.
        var binding = JsonSerializer.Serialize(new { prompt = json, guidance, profiles = profiles.Select(x => new { x.Key, x.Revision }), rules = rules.Select(x => new { x.Id, x.Revision }) });
        return new(json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding))), types, fields, truncated);
    }
}
