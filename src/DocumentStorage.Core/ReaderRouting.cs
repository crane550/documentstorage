using System.Collections.Concurrent;
using System.Text.Json;
using DocumentStorage.Data;

namespace DocumentStorage.Core;

public sealed class OllamaReader
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "http://127.0.0.1:11434/";
    public string Model { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
public sealed record ReaderHealth(string Name, string Endpoint, string Model, string State, DateTimeOffset? RetryAt, string? Error);
public sealed class ReaderUnavailableException(string message) : OllamaException(message, true);

public sealed partial class LibraryService
{
    public const int MaximumReaders = 6;
    private readonly ConcurrentDictionary<string, ReaderHealth> readerHealth = new();
    private static string ReaderKey(OllamaReader reader) => reader.Endpoint + "\n" + reader.Model;

    public static List<OllamaReader> ConfiguredReaders(OllamaSettings settings)
    {
        List<OllamaReader> fallbacks;
        try { fallbacks = JsonSerializer.Deserialize<List<OllamaReader>>(settings.FallbackReadersJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new StorageException("The reader list is invalid. Reload and save the AI reader settings."); }
        if (fallbacks.Count >= MaximumReaders || fallbacks.Any(x => x == null))
            throw new StorageException($"Use at most {MaximumReaders} readers.");
        return [new() { Name = settings.ReaderName, Endpoint = settings.Endpoint, Model = settings.Model }, .. fallbacks];
    }

    private static string ValidateReaders(OllamaSettings settings)
    {
        var readers = ConfiguredReaders(settings);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reader in readers)
        {
            if (reader.Name == null || reader.Endpoint == null || reader.Model == null) throw new StorageException("Each reader needs a name, server address and model.");
            reader.Name = reader.Name.Trim();
            reader.Endpoint = OllamaClient.ValidateEndpoint(reader.Endpoint);
            reader.Model = reader.Model.Trim();
            if (reader.Name.Length is 0 or > 80 || reader.Name.Any(char.IsControl)) throw new StorageException("Give each reader a name of 1–80 characters.");
            if (reader.Model.Length > 128 || reader.Model.Any(char.IsControl) || (settings.Enabled && reader.Enabled && reader.Model.Length == 0))
                throw new StorageException("Choose a locally installed model for each enabled reader.");
            if (reader.Model.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase)) throw new StorageException("Choose locally installed models, not cloud-backed models.");
            if (!seen.Add(ReaderKey(reader))) throw new StorageException("Each server and model combination should appear only once.");
        }
        if (settings.KeepAliveSeconds is < 0 or > 1800) throw new StorageException("Keep models loaded for 0–1800 seconds after use.");
        return JsonSerializer.Serialize(readers.Skip(1));
    }

    public List<ReaderHealth> ReaderHealthSnapshot(OllamaSettings settings) => ConfiguredReaders(settings).Select(reader =>
        !reader.Enabled ? new ReaderHealth(reader.Name, reader.Endpoint, reader.Model, "Disabled", null, null) :
        readerHealth.TryGetValue(ReaderKey(reader), out var health) ? health with { Name = reader.Name } :
        new ReaderHealth(reader.Name, reader.Endpoint, reader.Model, "Not checked", null, null)).ToList();

    // Callers hold the shared inference slot, never the filesystem writer. Probes send model
    // metadata only. A sleeping host does not spend the durable model-attempt budget.
    private async Task<T> WithReaderAsync<T>(OllamaSettings settings, IOllamaClient client,
        Func<OllamaReader, int, CancellationToken, Task<T>> read, CancellationToken token,
        Func<OllamaReader, Task>? selected = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var started = System.Diagnostics.Stopwatch.StartNew();
        OllamaException? inferenceFailure = null;
        foreach (var reader in ConfiguredReaders(settings).Where(x => x.Enabled))
        {
            token.ThrowIfCancellationRequested();
            var key = ReaderKey(reader);
            if (readerHealth.TryGetValue(key, out var previous) && previous.RetryAt > time.GetUtcNow()) continue;
            try
            {
                await client.ProbeAsync(reader.Endpoint, reader.Model, deadline.Token);
            }
            catch (OllamaException ex)
            {
                readerHealth[key] = new(reader.Name, reader.Endpoint, reader.Model, "Unavailable", time.GetUtcNow().AddSeconds(30), ex.Message);
                continue;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { break; }
            token.ThrowIfCancellationRequested();
            readerHealth[key] = new(reader.Name, reader.Endpoint, reader.Model, "Reading", null, null);
            try
            {
                if (selected != null) await selected(reader);
                var remaining = Math.Max(1, settings.TimeoutSeconds - (int)started.Elapsed.TotalSeconds);
                var result = await read(reader, remaining, deadline.Token);
                token.ThrowIfCancellationRequested();
                readerHealth[key] = new(reader.Name, reader.Endpoint, reader.Model, "Ready", null, null);
                return result;
            }
            catch (OllamaException ex) when (ex.Transient)
            {
                inferenceFailure = ex;
                readerHealth[key] = new(reader.Name, reader.Endpoint, reader.Model, "Unavailable", null, ex.Message);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                readerHealth[key] = new(reader.Name, reader.Endpoint, reader.Model, "Unavailable", time.GetUtcNow().AddSeconds(30), "The reading timed out.");
                throw new OllamaException("The local reading timed out. The saved job can retry.", true);
            }
            catch
            {
                readerHealth[key] = new(reader.Name, reader.Endpoint, reader.Model, "Ready", null, null);
                throw; // Bad structured output is not an excuse to repeatedly ask other models.
            }
        }
        token.ThrowIfCancellationRequested();
        if (inferenceFailure != null) throw inferenceFailure;
        throw new ReaderUnavailableException("Waiting for an available Ollama reader. Sleeping or offline computers will be checked again automatically.");
    }
}
