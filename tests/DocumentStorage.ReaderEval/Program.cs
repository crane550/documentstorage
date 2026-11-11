using System.Diagnostics;
using System.Text.Json;
using DocumentStorage.Core;

// Measures how well a real local model works out what kind of document something is, on a set
// of synthetic documents, the way an inbox would meet them: nothing learned at first, then a
// person approving or correcting each one. No catalog, no files moved, nothing kept.
//
//   dotnet run --project tests/DocumentStorage.ReaderEval -- --endpoint http://127.0.0.1:11434 --model qwen3:4b-instruct
//
// Options: --corpus <folder>   a folder with corpus.json and its text files (default: the synthetic set beside this tool)
//          --timeout <seconds> per question (default 600)        --show   print every question and answer

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
    if (args[i].StartsWith("--", StringComparison.Ordinal)) options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
var folder = options.GetValueOrDefault("corpus", Path.Combine(AppContext.BaseDirectory, "corpus"));
var manifest = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(Path.Combine(folder, "corpus.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var texts = manifest.ToDictionary(x => x.File, x => File.ReadAllText(Path.Combine(folder, x.File)));

if (!options.TryGetValue("endpoint", out var endpoint) || !options.TryGetValue("model", out var model))
{ Console.WriteLine("Give --endpoint and --model, for example --endpoint http://127.0.0.1:11434 --model qwen3:4b-instruct. See the top of Program.cs."); return; }

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var client = new OllamaClient(http);
var timeout = int.TryParse(options.GetValueOrDefault("timeout"), out var seconds) ? seconds : 600;
var show = options.ContainsKey("show");

// What Stash would know: the starter kinds, and whatever "the person" has confirmed so far.
var kinds = LibraryService.StarterKinds().Select(x => new ReadingKind(x.Name, x.Description, LibraryService.KindFields(x), new Dictionary<string, string>(), x.IsCatchAll)).ToList();
var examples = new List<ReadingExample>();
var groupKind = new Dictionary<string, string>();
var rows = new List<Row>();

// First the first document of every group (nothing like it has been seen), then the rest.
var order = manifest.GroupBy(x => x.Group).Select(x => x.First()).Concat(manifest.GroupBy(x => x.Group).SelectMany(x => x.Skip(1))).ToList();
var firsts = manifest.GroupBy(x => x.Group).Select(x => x.First().File).ToHashSet();
Console.WriteLine($"Reading {order.Count} synthetic documents with {model} at {endpoint}\n");
foreach (var item in order)
{
    var document = new ReadingDocument(item.File, "Inbox", texts[item.File]);
    var knowledge = new ReadingKnowledge(kinds, [], [], examples, "");
    var times = new List<(string Step, double Seconds)>();
    ReadingResult result;
    try
    {
        result = await DocumentReading.RunAsync(document, knowledge, async (step, block, question, schema, maxTokens) =>
        {
            var clock = Stopwatch.StartNew();
            var answer = await client.AskAsync(new(endpoint, model, timeout, step, block, question, schema, maxTokens));
            times.Add((step, clock.Elapsed.TotalSeconds));
            if (show) Console.WriteLine($"--- {step} ---\n{question}\n>>> {answer}\n");
            return answer;
        });
    }
    catch (OllamaException ex) { Console.WriteLine($"FAILED  {item.File}: {ex.Message}"); rows.Add(new(item, firsts.Contains(item.File), false, false, false, 0)); continue; }

    var reply = result.Reply;
    var proposedNew = reply.KindBasis == KindBasis.New;
    // What a person would file it as: one of the starter kinds, the kind this group already got, or a new one.
    var expected = item.Kind.Length > 0 ? item.Kind : groupKind.GetValueOrDefault(item.Group, "");
    var right = expected.Length > 0 ? !proposedNew && DocumentReading.SameName(reply.Kind, expected) : proposedNew;
    var settled = KindBasis.Settled(reply.KindBasis);
    rows.Add(new(item, firsts.Contains(item.File), right, expected.Length == 0, proposedNew, times.Sum(x => x.Seconds)));
    Console.WriteLine($"{(right ? "ok     " : "WRONG  ")}{item.File,-38} reader: \"{reply.What}\" -> {reply.Kind} ({reply.KindBasis}{(settled ? ", settled" : "")})" +
        (right ? "" : $"   expected: {(expected.Length > 0 ? expected : "a new kind")}") + $"   [{string.Join(", ", times.Select(x => $"{x.Step} {x.Seconds:0}s"))}]");

    // The person approves or corrects. What Stash keeps is what it keeps on a real approval: the
    // reader's name for the document beside the kind it was filed as.
    string filedAs;
    if (expected.Length > 0) filedAs = expected;
    else if (proposedNew) filedAs = reply.Kind;
    else filedAs = Title(item.Group); // squeezed into a kind it doesn't belong to: the person makes the new kind by hand
    if (kinds.All(x => !DocumentReading.SameName(x.Name, filedAs))) kinds.Add(new(filedAs, proposedNew ? reply.KindDescription : "", [], new Dictionary<string, string>(), false));
    if (item.Kind.Length == 0) groupKind[item.Group] = filedAs;
    filedAs = kinds.First(x => DocumentReading.SameName(x.Name, filedAs)).Name;
    examples.Insert(0, new(filedAs, reply.What, texts[item.File].Length > 400 ? texts[item.File][..400] : texts[item.File], reply.From, reply.Date, reply.Title, reply.Tags, new Dictionary<string, string>()));
}

Console.WriteLine();
Report("First of its type (nothing like it seen before)", rows.Where(x => x.First).ToList());
Report("Later ones (after a person approved or corrected the first)", rows.Where(x => !x.First).ToList());
var novel = rows.Where(x => x.ExpectedNew).ToList(); var known = rows.Where(x => !x.ExpectedNew).ToList();
Console.WriteLine($"New kinds: {novel.Count(x => x.ProposedNew)} of {novel.Count} documents that fit none of the kinds were proposed as a new kind; " +
    $"{known.Count(x => x.ProposedNew)} of {known.Count} that do fit a kind were wrongly proposed as new.");
Console.WriteLine($"Time: {rows.Sum(x => x.Seconds) / Math.Max(1, rows.Count):0} seconds a document on average.");
Console.WriteLine("\nWhat the reader called things, and the kinds a person filed them as:");
foreach (var name in examples.Where(x => x.ReaderWords.Length > 0).GroupBy(x => x.ReaderWords, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key))
    Console.WriteLine($"  \"{name.Key}\" -> {string.Join(", ", name.GroupBy(x => x.Kind).Select(x => $"{x.Key} ({x.Count()})"))}");

void Report(string title, List<Row> list) =>
    Console.WriteLine($"{title}: {list.Count(x => x.Right)} of {list.Count} kinds right ({(list.Count == 0 ? 0 : 100.0 * list.Count(x => x.Right) / list.Count):0}%).");

static string Title(string group) { var words = group.Replace('-', ' '); return char.ToUpperInvariant(words[0]) + words[1..]; }

sealed record Item(string File, string Group, string Kind);
sealed record Row(Item Item, bool First, bool Right, bool ExpectedNew, bool ProposedNew, double Seconds);
