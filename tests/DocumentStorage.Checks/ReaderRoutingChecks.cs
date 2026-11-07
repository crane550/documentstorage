using System.Net;
using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class ReaderRoutingChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch,
        Action<bool, string> check, Func<Func<Task>, string, Task> refuses)
    {
        var clock = new Clock();
        var library = new LibraryService(factory, new DocumentTextReader(), clock);
        Directory.CreateDirectory(scratch);
        var root = await library.AddRootAsync("Synthetic prioritized readers", scratch, false);
        var parent = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var intake = await library.CreateFolderAsync(parent.Id, "Incoming");
        await library.SetIntakeAsync(intake, true);
        await library.UploadAsync(intake, "scan.txt", new MemoryStream(Encoding.UTF8.GetBytes("Synthetic Cedar Bank statement. Statement ends 2026-10-31. Account 1234. Balance 42.50.")));
        clock.Advance(6); await library.ScanAsync();
        var doc = (await library.BrowseAsync(intake)).Items.Single();
        var settings = (await library.IdentificationOverviewAsync()).Settings;
        settings.Enabled = true; settings.AutoIdentify = false;
        settings.ReaderName = "Desktop"; settings.Endpoint = "http://desktop.test:11434/"; settings.Model = "synthetic:8b";
        settings.KeepAliveSeconds = 45;
        settings.FallbackReadersJson = JsonSerializer.Serialize(new[] { new OllamaReader { Name = "Dell", Endpoint = "http://dell.test:11434", Model = "synthetic:4b" } });
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner");
        async Task<IdentificationJob> Job(Guid id) { await using var db = await factory.CreateDbContextAsync(); return await db.IdentificationJobs.AsNoTracking().SingleAsync(x => x.Id == id); }
        async Task<Guid> Queue() { clock.Advance(1); return await library.QueueIdentificationAsync(doc.Id, "owner", true, sorting: true); }
        var client = new Fake();
        client.Offline.Add("desktop.test");
        var id = await Queue(); await library.ProcessNextIdentificationAsync(client);
        var saved = await Job(id);
        check(saved.State == "Succeeded" && saved.ReaderName == "Dell" && saved.Model == "synthetic:4b" && saved.Endpoint.Contains("dell.test"), "a sleeping preferred computer falls back and persists the actual server/model");
        check(client.Reads.Single().KeepAliveSeconds == 45 && client.Reads.Single().Endpoint.Contains("dell.test"), "fallback uses the configured idle-unload interval and actual endpoint");
        check(saved.Attempts == 1 && client.Probes.SequenceEqual(new[] { "desktop.test", "dell.test" }), "availability checks do not count as extra model attempts");
        check(!await library.ProcessNextIdentificationAsync(client) && client.Reads.Count == 1, "a saved successful fallback reading is not repeated");

        client.Offline.Clear(); client.Reads.Clear(); client.Probes.Clear(); clock.Advance(31);
        id = await Queue(); await library.ProcessNextIdentificationAsync(client);
        check((await Job(id)).ReaderName == "Desktop" && client.Reads.Single().Model == "synthetic:8b" && client.Probes.SequenceEqual(new[] { "desktop.test" }), "a recovered preferred reader is chosen again for the next document reading");

        client.Offline.UnionWith(new[] { "desktop.test", "dell.test" }); client.Reads.Clear();
        id = await Queue();
        for (var i = 0; i < 6; i++) { await library.ProcessNextIdentificationAsync(client); clock.Advance(31); }
        saved = await Job(id);
        check(saved.State == "Retry" && saved.Attempts == 0 && saved.LastError!.Contains("Waiting for an available"), "all readers offline keeps the durable job waiting beyond three checks without spending inference attempts");
        check(client.Reads.Count == 0 && File.Exists(Path.Combine(scratch, "Incoming/scan.txt")), "waiting for readers sends no document text and leaves the file in place");
        library = new LibraryService(factory, new DocumentTextReader(), clock);
        await library.RecoverIdentificationAsync(); client.Offline.Clear();
        await library.ProcessNextIdentificationAsync(client);
        check((await Job(id)).State == "Succeeded" && (await Job(id)).Attempts == 1, "waiting work survives service recreation and completes when a reader returns");

        client.Reads.Clear(); client.Probes.Clear(); client.FailDesktopDuringRead = true;
        id = await Queue(); await library.ProcessNextIdentificationAsync(client);
        saved = await Job(id);
        check(saved.State == "Succeeded" && saved.ReaderName == "Dell" && client.Reads.Count == 2 && saved.Attempts == 1, "a connection lost during inference falls back within the same bounded job cycle");
        client.FailDesktopDuringRead = false; client.InvalidReply = true; client.Reads.Clear();
        id = await Queue(); await library.ProcessNextIdentificationAsync(client);
        check((await Job(id)).State == "Failed" && (await Job(id)).Attempts == 1 && client.Reads.Count == 1, "invalid model output fails once rather than shopping other models for an answer");
        client.InvalidReply = false;

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeProbe = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        client.Probes.Clear(); client.Reads.Clear();
        id = await Queue(); var running = library.ProcessNextIdentificationAsync(client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await library.CancelIdentificationAsync(id, "owner"); await running.WaitAsync(TimeSpan.FromSeconds(5));
        check((await Job(id)).State == "Cancelled" && client.Reads.Count == 0 && client.Probes.Count == 1, "cancelling during an availability check prevents fallback and model inference");
        client.BeforeProbe = null;

        var fallback = LibraryService.ConfiguredReaders(settings)[1]; fallback.Enabled = false;
        settings.FallbackReadersJson = JsonSerializer.Serialize(new[] { fallback });
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner");
        library = new LibraryService(factory, new DocumentTextReader(), clock);
        client.Offline.Add("desktop.test"); client.Probes.Clear();
        id = await Queue(); await library.ProcessNextIdentificationAsync(client);
        check((await Job(id)).State == "Retry" && client.Probes.SequenceEqual(new[] { "desktop.test" }), "disabled fallback readers are never contacted");
        await library.CancelIdentificationAsync(id, "owner");

        settings.FallbackReadersJson = JsonSerializer.Serialize(new[] { new OllamaReader { Name = "Duplicate", Endpoint = settings.Endpoint, Model = settings.Model } });
        await refuses(() => library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner"), "duplicate server/model priority entries are refused");
        settings.FallbackReadersJson = "[null]";
        await refuses(() => library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner"), "malformed reader entries cannot enter the queue");
        settings.FallbackReadersJson = "[]"; settings.Enabled = false; settings.ReaderName = "Primary reader"; settings.Endpoint = "http://127.0.0.1:11434/"; settings.Model = ""; settings.KeepAliveSeconds = 120;
        await library.SaveOllamaSettingsAsync(settings, settings.Revision, "owner");
        await library.SetIntakeAsync(intake, false);
        await ProtocolAsync(check);
    }

    private static async Task ProtocolAsync(Action<bool, string> check)
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/api/show") return Json("""{"capabilities":["completion"]}""");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            check(payload.RootElement.GetProperty("keep_alive").GetInt32() == 0, "Ollama receives an explicit immediate-unload setting instead of a hardcoded keep-alive");
            return Json("""{"done":true,"message":{"content":"{\"kind\":\"Receipt\",\"title\":\"Synthetic purchase\"}"}}""");
        }));
        var client = new OllamaClient(http);
        await client.ProbeAsync("http://desktop.test:11434/", "synthetic:8b");
        check(paths.SequenceEqual(new[] { "/api/show" }), "availability probes request model metadata only, never chat or document text");
        await client.SortAsync(new("http://desktop.test:11434/", "synthetic:8b", 30, "{}", 0));
        using var cloudHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json("""{"remote_host":"https://cloud.example"}"""))));
        var refused = false;
        try { await new OllamaClient(cloudHttp).ProbeAsync("http://desktop.test:11434/", "synthetic:8b"); } catch (OllamaException) { refused = true; }
        check(refused, "reader probes reject cloud backing before a document can be sent");
        using var sleepingHttp = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json("{}"); }));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try { await new OllamaClient(sleepingHttp).ProbeAsync("http://desktop.test:11434/", "synthetic:8b"); } catch (OllamaException ex) { refused = ex.Transient; }
        check(refused && stopwatch.Elapsed.TotalSeconds is >= 2 and < 8, "a sleeping reader's metadata probe has a short independent timeout");
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Fake : IOllamaClient
    {
        public HashSet<string> Offline { get; } = [];
        public List<string> Probes { get; } = [];
        public List<SortRequest> Reads { get; } = [];
        public bool FailDesktopDuringRead { get; set; }
        public bool InvalidReply { get; set; }
        public Func<CancellationToken, Task>? BeforeProbe { get; set; }
        public async Task ProbeAsync(string endpoint, string model, CancellationToken cancellationToken = default)
        {
            var host = new Uri(endpoint).Host; Probes.Add(host);
            if (BeforeProbe != null) await BeforeProbe(cancellationToken);
            if (Offline.Contains(host)) throw new OllamaException("Synthetic sleeping computer.", true);
        }
        public Task<SortReply> SortAsync(SortRequest request, CancellationToken cancellationToken = default)
        {
            Reads.Add(request);
            if (FailDesktopDuringRead && new Uri(request.Endpoint).Host == "desktop.test") throw new OllamaException("Synthetic interrupted connection.", true);
            if (InvalidReply) throw new OllamaException("Synthetic invalid response.");
            return Task.FromResult(new SortReply("Bank statement", "", "Cedar Bank", "2026-10-31", "Checking statement", [], [], "medium"));
        }
        public Task<List<LocalModel>> ModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(new List<LocalModel>());
        public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
