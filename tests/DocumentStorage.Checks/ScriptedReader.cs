using System.Text.Json;
using DocumentStorage.Core;

// A stand-in for the local model. A check scripts what a whole reading should come to, and
// this answers each of the reading's questions from it: the reader's own name for the type of
// document (What, or the kind's name), whether a kind offered is the same thing, then the details.
internal static class ScriptedReader
{
    public static string Answer(AskRequest request, SortReply reading)
    {
        switch (request.Step)
        {
            case "describe":
                return JsonSerializer.Serialize(new { signs = reading.Signs ?? [], type = reading.What.Length > 0 ? reading.What : reading.Kind, about = reading.KindDescription });
            case "match":
                var offered = JsonSerializer.SerializeToElement(request.Schema).GetProperty("properties").GetProperty("nearest").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var same = offered.FirstOrDefault(x => string.Equals(x, reading.Kind, StringComparison.OrdinalIgnoreCase));
                return JsonSerializer.Serialize(new { nearest = same ?? offered[0], reason = "Synthetic comparison.", same = same != null });
            case "details":
                return JsonSerializer.Serialize(new { from = reading.From, date = reading.Date, title = reading.Title, tags = reading.Tags,
                    fields = reading.Fields.Select(x => new { name = x.Name, value = x.Value, quote = x.Quote }), confidence = reading.Confidence });
            default: throw new InvalidOperationException("An unknown reading step: " + request.Step);
        }
    }

    // The file the questions are about, from the document block every question starts with.
    public static string FileName(AskRequest request) =>
        request.Document.Split('\n').First(x => x.StartsWith("File name: ", StringComparison.Ordinal))["File name: ".Length..];
}

internal sealed class ScriptedModel(Func<AskRequest, SortReply> reading) : IOllamaClient
{
    public List<AskRequest> Asked { get; } = [];
    public Task<List<LocalModel>> ModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(new List<LocalModel>());
    public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Sorting never asks the old question.");
    public Task<string> AskAsync(AskRequest request, CancellationToken cancellationToken = default) { Asked.Add(request); return Task.FromResult(ScriptedReader.Answer(request, reading(request))); }
}
