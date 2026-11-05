using System.Net;
using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class IdentificationChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch,
        Action<bool,string> check, Func<Func<Task>,string,Task> refuses)
    {
        await ProtocolAsync(check);
        Directory.CreateDirectory(scratch);
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock);
        var root = await library.AddRootAsync("Local AI synthetic archive", scratch, false);
        var folder = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var intake = await library.CreateFolderAsync(folder.Id, "Incoming");
        await library.SetIntakeAsync(intake, true);
        await library.SaveInstructionsAsync(intake, "Cedar statements use the statement closing date, not the print date.");
        var settings = (await library.IdentificationOverviewAsync()).Settings;
        check(!settings.Enabled && !settings.AutoIdentify, "local AI and automatic identification are off by default");
        async Task<CatalogEntry> New(string name)
        {
            await library.UploadAsync(intake, name, new MemoryStream(Encoding.UTF8.GetBytes("Synthetic Cedar Bank statement. Account 1234. Statement ends 2026-10-31. Balance 42.50.")));
            clock.Advance(6); await library.ScanAsync();
            return (await library.BrowseAsync(intake)).Items.Single(x => x.Name == name);
        }
        async Task<IdentificationJob> Job(Guid id) { await using var db = await factory.CreateDbContextAsync(); return await db.IdentificationJobs.AsNoTracking().SingleAsync(x => x.Id == id); }
        async Task<Guid> Queue(Guid entry) { clock.Advance(1); return await library.QueueIdentificationAsync(entry, "owner", true); }
        var document = await New("statement.txt");
        await refuses(() => library.QueueIdentificationAsync(document.Id, "owner"), "disabled identification refuses queueing without contacting a model");
        settings.Enabled = true; settings.Model = "synthetic:latest";
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner");
        await refuses(() => library.SaveOllamaSettingsAsync(settings, settings.Revision - 1, "owner"), "stale local model settings cannot overwrite another edit");
        var profile = (await library.IdentificationOverviewAsync()).Types.Single(x => x.Key == "bank_statement");
        profile.Instructions += " Extract only the last four account digits.";
        profile = await library.SaveDocumentTypeAsync(profile, profile.Revision, "owner");
        await refuses(() => library.SaveDocumentTypeAsync(profile, profile.Revision - 1, "owner"), "document type guidance uses optimistic concurrency");
        await refuses(() => library.SaveDocumentTypeAsync(new() { Key = "bad", Name = "Bad", FactNamesJson = "[\"destination\"]" }, 0, "owner"), "document type fields cannot include file operations");
        var id = await Queue(document.Id);
        check(await library.QueueIdentificationAsync(document.Id, "owner") == id && await library.QueueIdentificationAsync(document.Id, "owner", true) == id, "duplicate queue clicks share a single active job");
        IdentificationResult Good() => new("bank_statement", new() { ["institution"] = "Cedar", ["statement_end"] = "2026-10-31" }, new() { ["institution"] = "Cedar Bank", ["statement_end"] = "Statement ends 2026-10-31" }, "Monthly statement.", false);
        var client = new Fake((input, _) =>
        {
            check(input.ContextJson.Contains("closing date") && input.ContextJson.Contains("last four account digits"), "compact model context includes inherited folder and document type guidance");
            check(!input.ContextJson.Contains(scratch) && !input.ContextJson.Contains("filename_template"), "identification context excludes server locations and file execution templates");
            return Task.FromResult(Good());
        });
        int operations;
        await using (var db = await factory.CreateDbContextAsync()) operations = await db.Operations.CountAsync();
        check(await library.ProcessNextIdentificationAsync(client), "persisted queued job is picked up by the worker service");
        var completed = await Job(id);
        check(completed.InputText.Contains("Cedar"), "reader persists the actual bounded input excerpt for authenticated diagnosis");
        if (completed.State != "Succeeded") throw new Exception($"Identification state {completed.State}: {completed.LastError}");
        var proposal = await library.UsableIdentificationAsync(id);
        check(proposal.State == "Succeeded" && proposal.Attempts == 1 && proposal.DocumentType == "bank_statement", "local model facts are persisted as a separate proposal");
        await using (var db = await factory.CreateDbContextAsync()) check(!await db.Reviews.AnyAsync(x => x.EntryId == document.Id) && await db.Operations.CountAsync() == operations && File.Exists(Path.Combine(scratch, "Incoming/statement.txt")), "a successful model response never approves, renames or moves a document");
        var review = await library.SaveReviewAsync(document.Id, 0, proposal.DocumentType, JsonSerializer.Deserialize<Dictionary<string,string>>(proposal.FactsJson)!, false, "owner");
        check(!(await library.PreviewFilingAsync(document.Id, "owner")).Ready, "imported facts still require explicit human review before filing");
        await refuses(() => library.UsableIdentificationAsync(id), "a saved manual review invalidates an earlier model proposal");

        // Inference never holds the filesystem writer. Cancellation wins over an uncooperative late response.
        var pending = new TaskCompletionSource<IdentificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new Fake((_, _) => { started.TrySetResult(); return pending.Task; });
        id = await Queue(document.Id); var processing = library.ProcessNextIdentificationAsync(blocked);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await library.CreateFolderAsync(folder.Id, "Still responsive").WaitAsync(TimeSpan.FromSeconds(3));
        check(!await library.ProcessNextIdentificationAsync(new Fake((_, _) => Task.FromResult(Good()))), "only one identification job runs at a time while library writes remain responsive");
        await library.CancelIdentificationAsync(id, "owner"); pending.SetResult(Good()); await processing;
        check((await Job(id)).State == "Cancelled", "cancelled work ignores even a late successful model response");

        async Task Stale(string label, Func<Task> edit)
        {
            var completion = new TaskCompletionSource<IdentificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobId = await Queue(document.Id);
            var running = library.ProcessNextIdentificationAsync(new Fake((_, _) => { ready.SetResult(); return completion.Task; }));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); await edit(); completion.SetResult(Good()); await running;
            check((await Job(jobId)).State == "Superseded", label);
        }
        await Stale("folder guidance edited during inference discards the stale response", () => library.SaveInstructionsAsync(intake, "Use the closing date. Updated guidance."));
        var rescanCancelled = false;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously); started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        id = await Queue(document.Id);
        processing = library.ProcessNextIdentificationAsync(new Fake((_, token) => { token.Register(() => rescanCancelled = true); started.SetResult(); return pending.Task; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await library.RescanDocumentAsync(document.Id, "owner", "Incoming/statement.txt");
        pending.SetResult(Good()); await processing;
        check(rescanCancelled && (await Job(id)).State == "Superseded", "forced text rescan cancels live inference and discards an uncooperative late response");
        await Stale("manual facts edited during inference are preserved", async () => { review = await library.SaveReviewAsync(document.Id, review.Revision, "bank_statement", new() { ["institution"] = "Manual choice" }, false, "owner"); });
        await Stale("source bytes changed without rescan invalidate the model response", () => File.AppendAllTextAsync(Path.Combine(scratch, "Incoming/statement.txt"), " Additional synthetic text."));
        clock.Advance(6); await library.ScanAsync(); clock.Advance(6); await library.ScanAsync();

        var retryId = await Queue(document.Id);
        var unavailable = new Fake((_, _) => throw new OllamaException("Synthetic temporary outage.", true));
        await library.ProcessNextIdentificationAsync(unavailable);
        check((await Job(retryId)).State == "Retry" && !await library.ProcessNextIdentificationAsync(unavailable), "transient model failures persist a delayed retry instead of spinning");
        clock.Advance(31); await library.ProcessNextIdentificationAsync(unavailable);
        clock.Advance(121); await library.ProcessNextIdentificationAsync(unavailable);
        check((await Job(retryId)).State == "Failed" && (await Job(retryId)).Attempts == 3, "transient model retry budget stops after three attempts");
        id = await Queue(document.Id);
        await library.ProcessNextIdentificationAsync(new Fake((_, _) => throw new OllamaException("Invalid model JSON.")));
        check((await Job(id)).State == "Failed" && (await Job(id)).Attempts == 1, "invalid structured output fails once without automatic token retries");

        id = await Queue(document.Id);
        await using (var db = await factory.CreateDbContextAsync()) { var job = await db.IdentificationJobs.SingleAsync(x => x.Id == id); job.State = "Running"; job.Attempts = 1; job.RunId = Guid.NewGuid(); job.LeaseUntil = clock.GetUtcNow().AddMinutes(5); await db.SaveChangesAsync(); }
        var restarted = new LibraryService(factory, new DocumentTextReader(), clock);
        await restarted.RecoverIdentificationAsync(); await restarted.ProcessNextIdentificationAsync(new Fake((_, _) => Task.FromResult(Good())));
        check((await Job(id)).State == "Succeeded" && (await Job(id)).Attempts == 2, "restart recovery retries the durable job and retains its attempt count");
        id = await Queue(document.Id);
        await using (var db = await factory.CreateDbContextAsync()) { var job = await db.IdentificationJobs.SingleAsync(x => x.Id == id); job.State = "Running"; job.Attempts = 3; await db.SaveChangesAsync(); }
        await restarted.RecoverIdentificationAsync();
        check((await Job(id)).State == "Failed", "repeated restarts cannot reset the model attempt limit");

        // Settings cancellation must stop HTTP work as well as the durable queue state.
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously); started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCancelled = false;
        id = await Queue(document.Id);
        processing = library.ProcessNextIdentificationAsync(new Fake((_, token) => { token.Register(() => tokenCancelled = true); started.SetResult(); return pending.Task; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.TimeoutSeconds++;
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner");
        pending.SetResult(Good()); await processing;
        check(tokenCancelled && (await Job(id)).State == "Cancelled", "changing local model settings cancels active HTTP work and ignores its late response");

        var noText = await New("no-text.bin");
        await refuses(() => library.QueueIdentificationAsync(noText.Id, "owner"), "files without indexed OCR are held for manual review rather than invented AI facts");
        var longName = "long-statement.txt";
        await library.UploadAsync(intake, longName, new MemoryStream(Encoding.UTF8.GetBytes("Synthetic Cedar Bank. " + new string('x', 8000) + " Statement ends 2026-10-31")));
        clock.Advance(6); await library.ScanAsync();
        var longDocument = (await library.BrowseAsync(intake)).Items.Single(x => x.Name == longName);
        settings.MaxInputCharacters = 2000;
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner");
        id = await Queue(longDocument.Id);
        await library.ProcessNextIdentificationAsync(new Fake((input, _) =>
        {
            check(input.InputText.Length <= 2000 && input.InputText.Contains("EXCERPT OMITTED") && input.InputText.EndsWith("2026-10-31") && input.ContextJson.Length <= 16000,
                "long documents use bounded head and tail OCR excerpts with a bounded guidance context");
            return Task.FromResult(Good());
        }));
        check((await Job(id)).InputTruncated && (await Job(id)).NeedsReview && (await Job(id)).InputText.Length <= 2000 && (await Job(id)).InputText.Contains("EXCERPT OMITTED"), "shortened OCR remains marked for review and the saved snapshot matches the submitted excerpt");
        await library.SaveReviewAsync(longDocument.Id, 0, "bank_statement", new() { ["institution"] = "Cedar" }, true, "owner");

        // A guidance edit outside the transmitted excerpt still changes the durable context binding.
        await library.SaveInstructionsAsync(intake, new string('a', 4000) + " original tail");
        await Stale("guidance edited beyond the prompt budget still invalidates an in-flight proposal", () => library.SaveInstructionsAsync(intake, new string('a', 4000) + " changed tail"));
        await library.SaveInstructionsAsync(intake, "Use the statement closing date.");
        var autoDoc = await New("automatic.txt");
        settings.AutoIdentify = true; settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner");
        review = (await library.IntakeAsync()).Items.Single(x => x.Entry.Id == document.Id).Review!;
        await library.SaveReviewAsync(document.Id, review.Revision, "bank_statement", new() { ["institution"] = "Manual choice" }, true, "owner");
        var before = (await library.IdentificationOverviewAsync()).Jobs.Count;
        await library.QueueAutomaticIdentificationAsync(0);
        var after = (await library.IdentificationOverviewAsync()).Jobs;
        check(after.Count == before + 1 && after.First().EntryId == autoDoc.Id && after.First().CreatedBy == "automatic", "automatic identification skips already reviewed documents and queues only intake proposals");
        await library.CancelIdentificationAsync(after.First().Id, "owner");
        await library.QueueAutomaticIdentificationAsync(0);
        check((await library.IdentificationOverviewAsync()).Jobs.Count == after.Count, "automatic identification respects a cancelled job until explicitly retried");
        settings.Enabled = false; settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner");
        check(!settings.AutoIdentify && !await library.ProcessNextIdentificationAsync(client), "turning local AI off also stops automatic identification");
        await library.SetIntakeAsync(intake, false);
    }

    private static async Task ProtocolAsync(Action<bool,string> check)
    {
        var input = new IdentificationRequest("http://127.0.0.1:11434/", "synthetic:latest", 30, "statement.txt", "{\"guidance\":\"Use the closing date\"}", "Cedar Bank\nStatement ends 2026-10-31", "Cedar Bank\nStatement ends 2026-10-31", false, ["bank_statement", "unknown"], ["institution", "statement_end"]);
        var valid = """{"document_type":"bank_statement","needs_review":false,"summary":"Statement","facts":[{"name":"institution","value":"Cedar","evidence":"Cedar Bank"}]}""";
        var parsed = OllamaClient.Parse(valid, input);
        check(parsed.Facts["institution"] == "Cedar" && !parsed.NeedsReview, "schema parser accepts facts supported by verbatim OCR evidence");
        parsed = OllamaClient.Parse(valid.Replace("Cedar Bank", "Invented quote"), input);
        check(parsed.Facts.Count == 0 && parsed.NeedsReview, "unsupported evidence is omitted and flagged for review");
        check(OllamaClient.Parse(valid, input with { Truncated = true }).NeedsReview, "shortened input always carries an uncertainty flag");
        foreach (var invalid in new[] { valid.Replace("\"summary\":", "\"execute\":\"move\",\"summary\":"), valid.Replace("institution", "destination"), valid.Replace("bank_statement", "made_up"), valid.Replace("\"needs_review\":false", "\"needs_review\":false,\"needs_review\":true"), "```json\n" + valid + "\n```" })
        {
            var refused = false; try { OllamaClient.Parse(invalid, input); } catch (OllamaException) { refused = true; }
            check(refused, "strict model schema rejects commands, unknown fields/types, duplicate fields or wrapped JSON");
        }
        var requests = new List<string>();
        var handler = new Handler(async (request, token) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/api/tags") return Json("""{"models":[{"name":"synthetic:latest","size":123},{"name":"remote:latest","remote_host":"https://remote.example","size":1},{"name":"huge-cloud","size":1}]}""");
            if (request.RequestUri.AbsolutePath == "/api/show") return Json("""{"capabilities":["completion"]}""");
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            check(payload.RootElement.GetProperty("format").GetProperty("additionalProperties").ValueKind == JsonValueKind.False && !payload.RootElement.GetProperty("stream").GetBoolean(), "Ollama chat uses a strict JSON schema and bounded non-streaming generation");
            var messages = payload.RootElement.GetProperty("messages");
            check(messages[0].GetProperty("content").GetString()!.Contains("untrusted evidence") && messages[1].GetProperty("content").GetString()!.Contains("document_text"), "OCR is sent as evidence with explicit instruction boundaries");
            return Json(JsonSerializer.Serialize(new { done = true, message = new { content = valid } }));
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; var client = new OllamaClient(http);
        check((await client.ModelsAsync(input.Endpoint)).Single().Name == "synthetic:latest", "model discovery filters cloud names and upstream metadata");
        check((await client.IdentifyAsync(input)).DocumentType == "bank_statement" && requests.SequenceEqual(new[] { "/api/tags", "/api/show", "/api/chat" }), "protocol checks model metadata before sending any OCR text");
        var chatSent = false;
        using var remoteHttp = new HttpClient(new Handler((request, _) => { if (request.RequestUri!.AbsolutePath == "/api/chat") chatSent = true; return Task.FromResult(Json("""{"remote_model":"remote-model","remote_host":"https://remote.example"}""")); }));
        var refusedRemote = false; try { await new OllamaClient(remoteHttp).IdentifyAsync(input); } catch (OllamaException) { refusedRemote = true; }
        check(refusedRemote && !chatSent, "cloud-backed model is rejected before document content is sent");
        using var largeHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', OllamaClient.MaximumResponseBytes + 1)) })));
        var refusedLarge = false; try { await new OllamaClient(largeHttp).IdentifyAsync(input); } catch (OllamaException) { refusedLarge = true; }
        check(refusedLarge, "model metadata response is bounded before parsing");
        using var closedHttp = new HttpClient(new Handler((_, _) => throw new IOException("Synthetic interrupted connection")));
        var interrupted = false; try { await new OllamaClient(closedHttp).IdentifyAsync(input); } catch (OllamaException ex) { interrupted = ex.Transient; }
        check(interrupted, "an interrupted response is retryable without exposing the raw connection exception");
        foreach (var endpoint in new[] { "file:///tmp/ollama", "http://user:secret@localhost:11434", "http://localhost:11434/api", "http://localhost:11434?token=secret" })
        {
            var refused = false; try { OllamaClient.ValidateEndpoint(endpoint); } catch (StorageException) { refused = true; }
            check(refused, "Ollama endpoint rejects credentials, non-HTTP schemes and request paths");
        }
        using var failedHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var transient = false; try { await new OllamaClient(failedHttp).IdentifyAsync(input); } catch (OllamaException ex) { transient = ex.Transient; }
        check(transient, "temporary Ollama HTTP failure is marked retryable without logging document content");
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Fake(Func<IdentificationRequest,CancellationToken,Task<IdentificationResult>> identify) : IOllamaClient
    {
        public Task<List<LocalModel>> ModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(new List<LocalModel>());
        public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken cancellationToken = default) => identify(request, cancellationToken);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
