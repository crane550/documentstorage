using System.Text;
using System.Text.Json;

namespace DocumentStorage.Core;

public sealed record SortField(string Name, string Value, string Quote);
// What a reading says about a document. What is the reader's own name for the type of document,
// given before any of the person's kinds were shown to it; KindBasis says how the kind was
// arrived at; KindOptions are the other kinds worth one click on the slip; Signs are the
// phrases in the document the reader went by.
public sealed record SortReply(string Kind, string KindDescription, string From, string Date, string Title, string[] Tags, SortField[] Fields, string Confidence,
    string What = "", string KindBasis = "", string[]? KindOptions = null, string[]? Signs = null);

// How a reading arrived at the kind.
public static class KindBasis
{
    // The reader's name for it is this kind's own name, or a name it has used before for documents
    // the person filed as this kind. Settled: nothing in the person's filing says otherwise.
    public const string Named = "named";
    // The reader's name was new, or has led to more than one kind before, so it was asked to choose. Waits for a person.
    public const string Chosen = "chosen";
    // None of the person's kinds: a new kind is proposed. Waits for a person.
    public const string New = "new";
    // Readings saved before kinds were decided this way carry no basis and keep counting as they did.
    public static bool Settled(string basis) => basis is "" or Named;
}

// What the reader may be told. All of it is the person's: their kinds, senders and tags, and
// documents they confirmed. Nothing about any one kind of document is written in code.
public sealed record ReadingKind(string Name, string Description, string[] Fields, IReadOnlyDictionary<string, string> FieldNotes, bool IsCatchAll);
// A document a person stands behind: what the reader called it, and what the person filed it as.
public sealed record ReadingExample(string Kind, string ReaderWords, string Text, string From, string Date, string Title, string[] Tags,
    IReadOnlyDictionary<string, string> Fields, string Lesson = "");
public sealed record ReadingKnowledge(IReadOnlyList<ReadingKind> Kinds, IReadOnlyList<string> Senders, IReadOnlyList<string> Tags, IReadOnlyList<ReadingExample> Examples, string Guidance);
public sealed record ReadingDocument(string FileName, string ArrivedIn, string Text, int TextBudget = DocumentReading.TextBudget);
public sealed record ReadingStep(string Name, string Question, string Answer);
public sealed record ReadingResult(SortReply Reply, IReadOnlyList<ReadingStep> Steps, string Document);
public delegate Task<string> AskReader(string step, string document, string question, object schema, int maxTokens);

// A reading is a short run of narrow questions about one document. Code decides what to ask next
// and checks every answer; the model never chooses what to do.
//
//   1. Describe  What type of document is this? Asked with none of the person's kinds on show,
//                so nothing is squeezed into a list.
//   2. Kind      (no model) The reader's name is one of the person's kinds, or a name it has given
//                before to documents the person always filed as one kind: that is the kind.
//   3. Match     (only when step 2 can't say) Which kind is nearest, and does this document really
//                belong in it? "No" proposes a new kind under the reader's own name.
//   4. Details   Sender, date, title, tags and that one kind's fields.
//
// What it learns is one thing: what the reader called a document, beside what the person filed it
// as. A name that has led to two kinds is no longer trusted by itself; the reader is then asked
// to choose between those kinds, shown one of each.
public static class DocumentReading
{
    public const int TextBudget = 6000;
    public const int MaxCandidates = 24;
    private const string TextStart = "----- document text -----";
    private const string TextEnd = "----- end of document text -----";

    public static async Task<ReadingResult> RunAsync(ReadingDocument doc, ReadingKnowledge know, AskReader ask)
    {
        var block = DocumentBlock(doc);
        var steps = new List<ReadingStep>();
        async Task<JsonElement> Step(string name, string question, object schema, int maxTokens)
        {
            var answer = await ask(name, block, question, schema, maxTokens);
            steps.Add(new(name, question, answer.Length > 4000 ? answer[..4000] : answer));
            try
            {
                using var json = JsonDocument.Parse(answer);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new OllamaException("The model did not follow the reading schema.");
                return json.RootElement.Clone();
            }
            catch (JsonException) { throw new OllamaException("The model did not return valid JSON."); }
        }

        // 1. What is it, in the reader's own words?
        var described = await Step("describe", DescribeQuestion(know), DescribeSchema, 260);
        var what = OneLine(Text(described, "type"), 60); var about = OneLine(Text(described, "about"), 300);
        var signs = Strings(described, "signs", 4, 120).Where(x => Found(doc.Text, x)).ToArray();
        if (what.Length == 0) throw new OllamaException("The model did not say what kind of document this is.");

        // 2. Where has that name led before?
        var led = WhereNameLed(what, know);
        string kind; string basis; var isNew = false; var options = new List<string>();
        if (led.Count > 0 && (led.Count == 1 || led[1].Count * 3 <= led[0].Count))
        { kind = led[0].Kind.Name; basis = KindBasis.Named; options.AddRange(led.Skip(1).Select(x => x.Kind.Name)); }
        else
        {
            // 3. A new name is compared with every kind; a name that has led to several is settled between those.
            var between = led.Count > 0;
            var candidates = between ? led.Select(x => x.Kind).ToList() : know.Kinds.Where(x => !x.IsCatchAll).Take(MaxCandidates).ToList();
            ReadingKind? nearest = null; var same = false;
            if (candidates.Count > 0)
            {
                var matched = await Step("match", MatchQuestion(what, about, candidates, know, between), MatchSchema(candidates), 160);
                nearest = Named(Text(matched, "nearest"), candidates);
                same = nearest != null && matched.TryGetProperty("same", out var s) && s.ValueKind == JsonValueKind.True;
            }
            options.AddRange(candidates.Where(x => between && x != nearest).Select(x => x.Name));
            if (nearest != null && (same || Named(what, know.Kinds) != null)) { kind = nearest.Name; basis = KindBasis.Chosen; }
            else { kind = what; basis = KindBasis.New; isNew = true; if (nearest != null) options.Insert(0, nearest.Name); }
        }

        // 4. The details, asked about that one kind.
        var filed = isNew ? null : Named(kind, know.Kinds);
        var details = ParseDetails(await Step("details", DetailsQuestion(kind, isNew ? about : filed?.Description ?? "", filed, know, LikeExamples(kind, doc, know)), DetailsSchema, 900));
        return new(new(kind, isNew ? about : "", details.From, details.Date, details.Title, details.Tags, details.Fields, details.Confidence, what, basis,
            options.Where(x => !SameName(x, kind)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray(), signs), steps, block);
    }

    // ---------- The document, shown the same way to every question ----------

    public static string DocumentBlock(ReadingDocument doc)
    {
        var builder = new StringBuilder("DOCUMENT\nFile name: ").Append(OneLine(doc.FileName, 200));
        if (doc.ArrivedIn.Length > 0) builder.Append("\nArrived in folder: ").Append(OneLine(doc.ArrivedIn, 200));
        return builder.Append('\n').Append(TextStart).Append('\n').Append(Excerpt(doc.Text, doc.TextBudget).Replace(TextStart, "").Replace(TextEnd, "")).Append('\n').Append(TextEnd).ToString();
    }

    // The start says what a document is; the end often holds totals, signatures and page counts.
    public static string Excerpt(string text, int budget)
    {
        if (text.Length <= budget) return text;
        var head = budget * 3 / 4;
        return text[..head] + $"\n[… {text.Length - budget} characters in the middle are not shown …]\n" + text[^(budget - head)..];
    }

    // ---------- What the person's filing says about a name ----------

    public sealed record NameLed(ReadingKind Kind, int Count);

    // The kinds a name has led to, most often first: the kind that carries the name itself, and
    // every kind the person filed a document as when the reader had called it that. More than
    // one kind close in count means the name alone no longer says which.
    public static List<NameLed> WhereNameLed(string what, ReadingKnowledge know)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Named(what, know.Kinds) is { } own) counts[NameKey(own.Name)] = 1;
        foreach (var example in know.Examples.Where(x => SameName(x.ReaderWords, what))) counts[NameKey(example.Kind)] = counts.GetValueOrDefault(NameKey(example.Kind)) + 1;
        return know.Kinds.Where(x => counts.ContainsKey(NameKey(x.Name))).Select(x => new NameLed(x, counts[NameKey(x.Name)])).OrderByDescending(x => x.Count).ToList();
    }

    // Earlier documents of the kind to show how the person likes its details: ones from a sender
    // this document names first, then the most recent.
    private static List<ReadingExample> LikeExamples(string kind, ReadingDocument doc, ReadingKnowledge know)
    {
        var text = " " + DocumentHints.Normalize(doc.FileName + " " + (doc.Text.Length > DocumentHints.TextWindow ? doc.Text[..DocumentHints.TextWindow] : doc.Text)) + " ";
        return know.Examples.Where(x => SameName(x.Kind, kind)).OrderByDescending(x => x.From.Length > 0 && text.Contains(" " + DocumentHints.Normalize(x.From) + " ", StringComparison.Ordinal)).Take(2).ToList();
    }

    // ---------- The questions ----------

    private static string DescribeQuestion(ReadingKnowledge know)
    {
        var q = new StringBuilder("QUESTION\nWhat type of document is this?\n\n" +
            "Work it out from what the document is and what it is for, not from single words in it. Name the type the way a person would label a folder of documents like it.\n\n" +
            "signs: two to four short phrases copied exactly from the document that show what type it is.\n" +
            "type: the type of document in one to four plain words, singular, such as \"Lab result\", \"Sheet music\", \"User manual\", \"Lease agreement\" or \"Subscription renewal\". " +
            "Be as specific as the document allows. Never put a company, a person, a place, a date or a number in it.\n" +
            "about: one sentence saying what documents of this type are and what they are for in general, not about this one.");
        if (know.Guidance.Trim().Length > 0) q.Append("\n\nThe person's own notes on their filing: ").Append(OneLine(know.Guidance, 800));
        return q.ToString();
    }

    private static string MatchQuestion(string what, string about, List<ReadingKind> candidates, ReadingKnowledge know, bool between)
    {
        var q = new StringBuilder("QUESTION\nThis document was described as \"").Append(what).Append('"');
        if (about.Length > 0) q.Append(": ").Append(about);
        q.Append(between
            ? "\n\nDocuments described that way have been filed by this person as different kinds. The kinds, with one of each:"
            : "\n\nThese are the kinds of document this person files:");
        foreach (var kind in candidates)
        {
            q.Append("\n- ").Append(kind.Name);
            if (kind.Description.Length > 0) q.Append(": ").Append(OneLine(kind.Description, 200));
            var mine = know.Examples.Where(x => SameName(x.Kind, kind.Name)).ToList();
            if (between)
            {
                // The telling example is one the reader gave this very name; failing that, any of the kind.
                if ((mine.FirstOrDefault(x => SameName(x.ReaderWords, what)) ?? mine.FirstOrDefault()) is { } example)
                {
                    q.Append(" One the person filed here began: \"").Append(OneLine(example.Text, 220)).Append('"');
                    if (example.Lesson.Length > 0) q.Append(" Note: ").Append(OneLine(example.Lesson, 240));
                }
            }
            else if (mine.Select(x => x.ReaderWords).Where(x => x.Length > 0 && !SameName(x, kind.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray() is { Length: > 0 } called)
                q.Append(" Also called: ").Append(string.Join(", ", called)).Append('.');
        }
        q.Append("\n\nnearest: the kind from the list that comes closest to this document.\n" +
            "reason: one short sentence comparing this document with that kind.\n" +
            "same: true when this document belongs in that kind, so that a person looking for it would look there. " +
            "false when it is a different type of document that only shares some words, a subject or a sender with that kind. It then gets a kind of its own, which is fine: never force a document into a kind it does not belong to.");
        return q.ToString();
    }

    private static string DetailsQuestion(string kind, string description, ReadingKind? filed, ReadingKnowledge know, List<ReadingExample> like)
    {
        var q = new StringBuilder("QUESTION\nThis document is filed as \"").Append(kind).Append('"');
        if (description.Length > 0) q.Append(" (").Append(OneLine(description, 200).TrimEnd('.')).Append(')');
        q.Append(". Read its details.\n\n" +
            "from: who sent, issued or wrote it (a company, office or person), not the person it was sent to. Use the exact spelling from the known senders when it is one of them. Empty if not shown.\n" +
            "date: the document's own date as yyyy-MM-dd (statement end, invoice or order date, letter date, service date), not a print or due date unless it is the only one. Empty if no date is shown; never guess one.\n" +
            "title: two to six words that would help find it later, without the sender or the date.\n" +
            "tags: zero to four of the known tags that clearly apply; add a new lowercase tag only when it would obviously group documents (a vehicle, a property, a person, a tax year like tax-2026).\n");
        if (filed == null)
            q.Append("fields: up to four values worth keeping from a document of this type (a number that identifies it, a total, a date that matters), each named in one to three words, with a short quote copied exactly from the text.\n");
        else if (filed.Fields.Length == 0) q.Append("fields: leave empty.\n");
        else
        {
            q.Append("fields: the values of the fields below that are written in the document, each with a short quote copied exactly from the text. A date field takes yyyy-MM-dd; money takes digits with a decimal point and no currency sign. Leave out fields that are not written.\n");
            foreach (var field in filed.Fields) q.Append("  - ").Append(field).Append(filed.FieldNotes.TryGetValue(field, out var note) ? ": " + note : "").Append('\n');
        }
        q.Append("confidence: high only when the sender and the date are both plainly written; medium when one is inferred; low otherwise.");
        if (know.Senders.Count > 0) q.Append("\n\nKnown senders: ").Append(string.Join("; ", know.Senders));
        if (know.Tags.Count > 0) q.Append("\nKnown tags: ").Append(string.Join(", ", know.Tags));
        if (like.Count > 0)
        {
            q.Append("\n\nDetails the person approved for earlier documents of this kind, as examples of how they like them:");
            foreach (var item in like)
            {
                q.Append("\n- from \"").Append(item.From).Append("\", date ").Append(item.Date.Length > 0 ? item.Date : "none").Append(", title \"").Append(OneLine(item.Title, 80)).Append('"');
                if (item.Tags.Length > 0) q.Append(", tags ").Append(string.Join(", ", item.Tags.Take(4)));
                if (item.Fields.Count > 0) q.Append(", fields ").Append(string.Join(", ", item.Fields.Take(4).Select(x => x.Key + " = " + OneLine(x.Value, 60))));
                q.Append(". It began: \"").Append(OneLine(item.Text, 160)).Append('"');
                if (item.Lesson.Length > 0) q.Append(" Note: ").Append(OneLine(item.Lesson, 240));
            }
        }
        if (know.Guidance.Trim().Length > 0) q.Append("\n\nThe person's own notes on their filing: ").Append(OneLine(know.Guidance, 800));
        return q.ToString();
    }

    private static readonly object DescribeSchema = new
    {
        type = "object", additionalProperties = false, required = new[] { "signs", "type", "about" },
        properties = new
        {
            signs = new { type = "array", maxItems = 4, items = new { type = "string", maxLength = 120 } },
            type = new { type = "string", maxLength = 60 },
            about = new { type = "string", maxLength = 300 }
        }
    };

    private static object MatchSchema(List<ReadingKind> candidates) => new
    {
        type = "object", additionalProperties = false, required = new[] { "nearest", "reason", "same" },
        properties = new
        {
            nearest = new { type = "string", @enum = candidates.Select(x => x.Name).ToArray() },
            reason = new { type = "string", maxLength = 200 },
            same = new { type = "boolean" }
        }
    };

    private static readonly object DetailsSchema = new
    {
        type = "object", additionalProperties = false, required = new[] { "from", "date", "title", "tags", "fields", "confidence" },
        properties = new
        {
            from = new { type = "string", maxLength = 120 },
            date = new { type = "string", pattern = "^(\\d{4}-\\d{2}-\\d{2})?$" },
            title = new { type = "string", maxLength = 80 },
            tags = new { type = "array", maxItems = 5, items = new { type = "string", maxLength = 40 } },
            fields = new
            {
                type = "array", maxItems = 12,
                items = new
                {
                    type = "object", additionalProperties = false, required = new[] { "name", "value", "quote" },
                    properties = new { name = new { type = "string", maxLength = 64 }, value = new { type = "string", maxLength = 200 }, quote = new { type = "string", maxLength = 200 } }
                }
            },
            confidence = new { type = "string", @enum = new[] { "high", "medium", "low" } }
        }
    };

    // ---------- Reading the answers ----------

    public sealed record DetailsAnswer(string From, string Date, string Title, string[] Tags, SortField[] Fields, string Confidence);

    public static DetailsAnswer ParseDetails(string content)
    {
        try { using var json = JsonDocument.Parse(content); return ParseDetails(json.RootElement); }
        catch (JsonException) { throw new OllamaException("The model did not follow the reading schema."); }
    }

    private static DetailsAnswer ParseDetails(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new OllamaException("The model did not follow the reading schema.");
        var fields = new List<SortField>();
        if (root.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array)
            foreach (var item in f.EnumerateArray().Take(12))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = Bound(Text(item, "name"), 64).ToLowerInvariant(); var value = Bound(Text(item, "value"), 300);
                if (name.Length > 0 && value.Length > 0 && fields.All(x => x.Name != name)) fields.Add(new(name, value, Bound(Text(item, "quote"), 300)));
            }
        var confidence = Bound(Text(root, "confidence"), 10).ToLowerInvariant();
        if (confidence is not ("high" or "medium" or "low")) confidence = "low";
        return new(Bound(Text(root, "from"), 120), Bound(Text(root, "date"), 10), Bound(Text(root, "title"), 120),
            Strings(root, "tags", 6, 60).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), fields.ToArray(), confidence);
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static IEnumerable<string> Strings(JsonElement element, string name, int most, int length) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Bound(x.GetString()!, length)).Where(x => x.Length > 0).Take(most).ToArray() : [];
    public static string Bound(string value, int max)
    {
        value = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return value.Length > max ? value[..max].Trim() : value;
    }
    private static string OneLine(string value, int max) => Bound(string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), max);
    private static bool Found(string text, string quote)
    {
        static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var q = Squash(quote);
        return q.Length >= 2 && Squash(text).Contains(q, StringComparison.Ordinal);
    }

    // ---------- Names of kinds ----------

    private static string NameKey(string text)
    {
        var name = DocumentHints.Normalize(text);
        return name.Length > 3 && name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) ? name[..^1] : name;
    }
    // The same name, whatever its capitals, spacing or plural.
    public static bool SameName(string a, string b) => a.Length > 0 && NameKey(a) == NameKey(b);
    private static ReadingKind? Named(string text, IEnumerable<ReadingKind> kinds) => Named(text, kinds, x => x.Name);

    // A kind by its own name, exact first, then singular or plural.
    public static T? Named<T>(string text, IEnumerable<T> kinds, Func<T, string> name) where T : class
    {
        var key = NameKey(text);
        if (key.Length == 0) return null;
        var list = kinds as IReadOnlyList<T> ?? kinds.ToList();
        var exact = DocumentHints.Normalize(text);
        return list.FirstOrDefault(x => DocumentHints.Normalize(name(x)) == exact) ?? list.FirstOrDefault(x => NameKey(name(x)) == key);
    }
}
