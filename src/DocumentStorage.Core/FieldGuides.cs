using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

// How a kind's field is found: its type, one line on what to look for, and the words usually
// printed beside it. The reader is told all three; code uses the words to find values on its own.
public sealed record FieldGuide(string Type, string Look, string[] Labels);
// What looking for fields found: values with the quote each came from, where each came from
// ("text" when code found it by its printed words, "reader" when the model did), and notes.
public sealed record FieldFinding(Dictionary<string, string> Values, Dictionary<string, string> Quotes, Dictionary<string, string> Sources, string[] Notes, bool AskedReader);
// A field the reader suggests for a kind, with this document's value when it could be checked.
public sealed record FieldDraft(string Key, FieldGuide Guide, string Example, string Quote, bool Existing);
public sealed record KindDraft(string Kind, string Description, List<FieldDraft> Fields, string ContentHash);

public sealed partial class LibraryService
{
    public static readonly string[] FieldTypes = ["text", "date", "money", "number", "id"];
    public const int FieldTextBudget = 12000;
    private static readonly HashSet<string> DetailKeys = ["kind", "from", "sender", "date", "title", "tags", "tag", "name", "folder"];
    private readonly SemaphoreSlim fieldReads = new(1, 1);

    public static string FieldTypeLabel(string type) => type switch
    {
        "date" => "Date", "money" => "Money", "number" => "Number", "id" => "ID number", _ => "Text"
    };

    // What a field holds when nobody has said: by the words in its name.
    public static string DefaultFieldType(string key)
    {
        var words = key.Split('_');
        if (words.Contains("date")) return "date";
        if (words.Any(w => w is "total" or "balance" or "amount" or "payment" or "price" or "cost" or "fee" or "fees" or "paid" or "owed" or "charges" or "subtotal")) return "money";
        if (words.Any(w => w is "number" or "no" or "ending" or "suffix" or "id" or "policy" or "reference" or "ref" or "vin")) return "id";
        if (words.Any(w => w is "year" or "count" or "quantity" or "qty")) return "number";
        return "text";
    }
    public static FieldGuide DefaultGuide(string key) => new(DefaultFieldType(key), "", []);

    public static Dictionary<string, FieldGuide> ReadGuides(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, FieldGuide>>(json) ?? []; } catch (JsonException) { return []; }
    }
    // Every field the kind carries, described or not.
    public static Dictionary<string, FieldGuide> FieldGuides(DocumentKind kind)
    {
        var stored = ReadGuides(kind.FieldGuideJson);
        return KindFields(kind).Distinct().ToDictionary(k => k, k => stored.TryGetValue(k, out var g) ? CleanGuide(k, g) : DefaultGuide(k));
    }
    public static FieldGuide GuideFor(DocumentKind? kind, string key) =>
        kind != null && ReadGuides(kind.FieldGuideJson).TryGetValue(key, out var g) ? CleanGuide(key, g) : DefaultGuide(key);
    public static bool Described(FieldGuide guide) => guide.Look.Length > 0 || guide.Labels.Length > 0;

    public static FieldGuide CleanGuide(string key, FieldGuide guide)
    {
        var type = (guide.Type ?? "").Trim().ToLowerInvariant();
        if (!FieldTypes.Contains(type)) type = DefaultFieldType(key);
        var labels = (guide.Labels ?? []).Select(x => OneLine(x ?? "", 40)).Where(x => x.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
        return new(type, OneLine(guide.Look ?? "", 240), labels);
    }
    public static string[] SplitLabels(string text) => text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string OneLine(string text, int max)
    {
        var line = string.Join(' ', new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length > max ? line[..max].Trim() : line;
    }

    // A value as kept: a date as yyyy-MM-dd, money as 1234.56; anything else as written.
    public static string NormalizeFieldValue(string key, string value, FieldGuide guide)
    {
        value = value.Trim();
        if (value.Length == 0) return value;
        return guide.Type switch
        {
            "date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", out _) ? value : DocumentHints.FindDates(value, "value").FirstOrDefault()?.Date.ToString("yyyy-MM-dd") ?? value,
            "money" => MoneyText(value) ?? value,
            // A masked number keeps the digits it shows: ****5678 and XXXX-5678 are both 5678.
            "id" => MaskedId().Match(value) is { Success: true } masked ? masked.Groups["shown"].Value : value,
            _ => value
        };
    }

    public static string? MoneyText(string value)
    {
        var m = MoneyPattern().Match(value.Trim());
        if (!m.Success || !decimal.TryParse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
        var negative = (m.Groups["open"].Success && m.Groups["close"].Success) || m.Groups["neg"].Success || m.Groups["neg2"].Success || m.Groups["sign"].Value == "-";
        return (negative && amount != 0 ? "-" : "") + amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    // Is this value written in the text, allowing for how each type is commonly written:
    // $1,234.56 for 1234.56, May 16, 2025 for 2025-05-16, ****1234 for XXXX1234, any capitals or spacing.
    public static bool ValueFound(string text, string value, FieldGuide guide)
    {
        value = value.Trim();
        if (value.Length == 0) return false;
        if (QuoteFound(text, value)) return true;
        switch (guide.Type)
        {
            case "date":
                var normal = NormalizeFieldValue("", value, guide);
                return DateOnly.TryParseExact(normal, "yyyy-MM-dd", out var date) && DocumentHints.FindDates(text, "text").Any(x => x.Date == date);
            case "money" or "number":
                if (MoneyText(value) is not string wanted) return false;
                var amount = Math.Abs(decimal.Parse(wanted, CultureInfo.InvariantCulture));
                return NumberToken().Matches(text).Select(x => MoneyText(x.Value)).OfType<string>().Any(x => Math.Abs(decimal.Parse(x, CultureInfo.InvariantCulture)) == amount);
            case "id":
                var letters = Alnum(value); var digits = new string(value.Where(char.IsDigit).ToArray());
                return text.Split('\n').Any(line => letters.Length >= 3 && Alnum(line).Contains(letters, StringComparison.Ordinal)
                    || digits.Length >= 4 && new string(line.Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal));
            default:
                var plain = Alnum(value);
                return plain.Length >= 3 && Alnum(text).Contains(plain, StringComparison.Ordinal);
        }
    }
    private static string Alnum(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Finds a value by the words printed beside it ("Beginning balance $1,204.33"): the rest of the
    // line after the words, or the next line when they stand alone, read as the field's type.
    public static (string Value, string Quote)? LabelFind(string text, FieldGuide guide)
    {
        var lines = text.Replace("\r", "").Split('\n');
        foreach (var label in guide.Labels)
        {
            var pattern = new Regex((char.IsLetterOrDigit(label[0]) ? @"(?<![\p{L}\p{N}])" : "") + Regex.Escape(label).Replace(@"\ ", @"\s+")
                + (char.IsLetterOrDigit(label[^1]) ? @"(?![\p{L}\p{N}])" : ""), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            for (var i = 0; i < lines.Length; i++)
            {
                var match = pattern.Match(lines[i]);
                if (!match.Success) continue;
                var rest = lines[i][(match.Index + match.Length)..].TrimStart(' ', '\t', ':', '.', '-', '#', '=');
                var quoteLine = lines[i].Trim();
                if (rest.Trim().Length == 0 && i + 1 < lines.Length) { rest = lines[i + 1].Trim(); quoteLine = quoteLine + " " + rest; }
                if (ValueAfterLabel(rest, guide) is string value)
                    return (value, quoteLine.Length > 160 ? quoteLine[..160] : quoteLine);
            }
        }
        return null;
    }
    private static string? ValueAfterLabel(string rest, FieldGuide guide)
    {
        rest = rest.Trim();
        if (rest.Length == 0) return null;
        switch (guide.Type)
        {
            case "money":
                return NumberToken().Matches(rest).Select(x => MoneyText(x.Value)).OfType<string>().FirstOrDefault();
            case "number":
                var number = NumberToken().Match(rest);
                return number.Success ? number.Value.Trim().TrimStart('$', '€', '£', '(').TrimEnd(')').Replace(",", "") : null;
            case "date":
                return DocumentHints.FindDates(rest, "text").OrderBy(x => rest.IndexOf(x.Text, StringComparison.Ordinal)).FirstOrDefault()?.Date.ToString("yyyy-MM-dd");
            case "id":
                var id = IdToken().Match(rest);
                return id.Success ? id.Value : null;
            default:
                var cell = ColumnGap().Split(rest)[0].Trim().TrimEnd('.', ',', ';');
                if (cell.Length > 80) cell = cell[..80].Trim();
                return cell.Length >= 2 ? cell : null;
        }
    }

    // What the reader is shown of a long document: the start, then the passages around the printed
    // words of the fields being looked for, so a balance on page three is still seen.
    public static string FieldExcerpt(string text, IEnumerable<string> labels, int budget)
    {
        if (text.Length <= budget) return text;
        var head = budget * 2 / 3;
        var windows = new List<(int Start, int End)>();
        foreach (var label in labels.Where(x => x.Length >= 3))
        {
            var at = head;
            while ((at = text.IndexOf(label, at, StringComparison.OrdinalIgnoreCase)) >= 0 && windows.Count < 20)
            {
                windows.Add((Math.Max(head, at - 200), Math.Min(text.Length, at + label.Length + 300)));
                at += label.Length;
            }
        }
        var builder = new StringBuilder(text[..head]);
        var end = head;
        foreach (var (start, stop) in windows.OrderBy(x => x.Start))
        {
            var from = Math.Max(start, end);
            if (from >= stop) continue;
            var piece = text[from..stop];
            if (builder.Length + piece.Length + 8 > budget) break;
            if (from > end) builder.Append("\n[…]\n");
            builder.Append(piece); end = stop;
        }
        builder.Append("\n[the rest of the document is not shown]");
        return builder.ToString();
    }

    // How a field is described to the reader in a sorting prompt.
    private static string FieldNote(string key, FieldGuide guide)
    {
        var note = FieldLabel(key) + " (" + guide.Type + ")";
        if (guide.Look.Length > 0) note += ": " + guide.Look;
        if (guide.Labels.Length > 0) note += " Printed as: " + string.Join(", ", guide.Labels.Take(4)) + ".";
        return note.Length > 220 ? note[..220] : note;
    }

    // Fills a card's empty fields from the words printed beside them, before anyone asks.
    private static void FillFromLabels(CatalogEntry entry, DocumentKind? kind, Dictionary<string, string> fields, Dictionary<string, string> sources, Dictionary<string, string> quotes)
    {
        if (kind == null || entry.ExtractedText.Length == 0) return;
        foreach (var (key, guide) in FieldGuides(kind))
        {
            if (guide.Labels.Length == 0 || fields.TryGetValue(key, out var have) && have.Trim().Length > 0) continue;
            if (LabelFind(entry.ExtractedText, guide) is not { } found) continue;
            fields[key] = NormalizeFieldValue(key, found.Value, guide); quotes[key] = found.Quote; sources["field:" + key] = "text";
        }
    }

    // ---------- Changing a kind's guides ----------

    public async Task<DocumentKind> SetFieldGuideAsync(string kindName, string field, FieldGuide guide, string actor)
    {
        Actor(actor);
        var key = FactKey(field);
        return await ChangeKindAsync(kindName, kind =>
        {
            if (!KindFields(kind).Contains(key)) throw new StorageException("Keep the field for this kind before describing it.");
            var guides = ReadGuides(kind.FieldGuideJson);
            var clean = CleanGuide(key, guide);
            if (guides.TryGetValue(key, out var old) && old == clean) return false;
            guides[key] = clean; kind.FieldGuideJson = JsonSerializer.Serialize(guides);
            return true;
        });
    }

    // Saves what a person kept from "Set up fields with AI": new fields are added, described ones get
    // their guides, and nothing already on the kind is removed. A kind not made yet is made now, with
    // the slip's arrangement, because the person asked for it by name.
    public async Task<DocumentKind> SaveKindDraftAsync(string kindName, string? description, IReadOnlyList<(string Key, FieldGuide Guide)> fields, string folderPattern, string namePattern, string actor)
    {
        Actor(actor);
        var chosen = fields.Select(x => (Key: FactKey(x.Key), Guide: CleanGuide(FactKey(x.Key), x.Guide))).Where(x => !ReservedAiFacts.Contains(x.Key) && !DetailKeys.Contains(x.Key))
            .DistinctBy(x => x.Key).ToList();
        if (description != null && description.Length > 400) throw new StorageException("Keep the description under 400 characters.");
        await using (var db = await factory.CreateDbContextAsync())
        {
            if (MatchKind(kindName, await db.Kinds.AsNoTracking().ToListAsync()) == null)
            {
                if (chosen.Count > KindFieldLimit) throw new StorageException($"A kind can carry up to {KindFieldLimit} fields. Untick some.");
                return await SaveKindAsync(new()
                {
                    Name = kindName, Description = description?.Trim() ?? "", FolderPattern = folderPattern, NamePattern = namePattern,
                    FieldsJson = JsonSerializer.Serialize(chosen.Select(x => x.Key)), FieldGuideJson = JsonSerializer.Serialize(chosen.ToDictionary(x => x.Key, x => x.Guide))
                }, actor);
            }
        }
        return await ChangeKindAsync(kindName, kind =>
        {
            var keys = KindFields(kind).ToList();
            foreach (var item in chosen) if (!keys.Contains(item.Key)) keys.Add(item.Key);
            if (keys.Count > KindFieldLimit) throw new StorageException($"A kind can carry up to {KindFieldLimit} fields. Untick some, or remove one on the Kinds page.");
            var guides = ReadGuides(kind.FieldGuideJson);
            foreach (var item in chosen) guides[item.Key] = item.Guide;
            kind.FieldsJson = JsonSerializer.Serialize(keys); kind.FieldGuideJson = JsonSerializer.Serialize(guides);
            if (description != null && description.Trim().Length > 0) kind.Description = description.Trim();
            return true;
        });
    }

    // ---------- Asking the reader from the slip ----------

    private async Task<(CatalogEntry Entry, OllamaSettings Settings, DocumentKind? Kind)> FieldReadingAsync(Guid entryId, string kindName)
    {
        await using var db = await factory.CreateDbContextAsync();
        var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId && !x.IsFolder && !x.IsMissing && x.TrashBatchId == null)
            ?? throw new StorageException("That document is no longer in the library.");
        if (entry.TextStatus != "Indexed" || entry.ExtractedText.Trim().Length == 0) throw new StorageException("This document has no text to read yet.");
        var settings = await db.OllamaConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1) ?? new();
        var kind = MatchKind(kindName, await db.Kinds.AsNoTracking().ToListAsync());
        return (entry, settings, kind);
    }

    private async Task<T> AskReaderAsync<T>(Guid entryId, string contentHash, int settingsRevision, int timeoutSeconds, Func<CancellationToken, Task<T>> ask, CancellationToken cancellationToken)
    {
        if (!await fieldReads.WaitAsync(TimeSpan.Zero, cancellationToken)) throw new StorageException("The reader is already looking at a document for you. Wait for it to finish.");
        try
        {
            if (!await modelWork.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 120)), cancellationToken))
                throw new StorageException("The reader is busy with another document. Try again in a minute.");
            T answer;
            try { answer = await ask(cancellationToken); }
            catch (OllamaException ex) { throw new StorageException(ex.Message); }
            finally { modelWork.Release(); }
            // The answer is about this exact text under these settings; anything changed means asking again.
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var now = await db.Entries.AsNoTracking().Where(x => x.Id == entryId).Select(x => x.ContentHash).SingleOrDefaultAsync(cancellationToken);
            var settings = await db.OllamaConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
            if (now != contentHash) throw new StorageException("The document changed while the reader was looking. Ask again.");
            if (settings == null || !settings.Enabled || settings.Revision != settingsRevision) throw new StorageException("The reader's settings changed while it was looking. Ask again.");
            return answer;
        }
        finally { fieldReads.Release(); }
    }

    // Find with AI: looks only for the fields asked for. Code tries each field's printed words first;
    // the reader is asked for the rest, told what each one is, and shown the parts of the document
    // where those words appear. Every value is checked against the text before it is offered.
    public async Task<FieldFinding> FindFieldsAsync(Guid entryId, string kindName, IReadOnlyCollection<string> fields, string from, DateOnly? date, string actor, IOllamaClient client, CancellationToken cancellationToken = default)
    {
        Actor(actor);
        var keys = fields.Select(FactKey).Where(x => !ReservedAiFacts.Contains(x)).Distinct().Take(KindFieldLimit + 4).ToList();
        if (keys.Count == 0) throw new StorageException("There are no empty fields to look for.");
        var (entry, settings, kind) = await FieldReadingAsync(entryId, kindName);
        var guides = keys.ToDictionary(k => k, k => GuideFor(kind, k));
        var values = new Dictionary<string, string>(); var quotes = new Dictionary<string, string>(); var sources = new Dictionary<string, string>(); var notes = new List<string>();
        foreach (var key in keys)
            if (LabelFind(entry.ExtractedText, guides[key]) is { } found)
            { values[key] = NormalizeFieldValue(key, found.Value, guides[key]); quotes[key] = found.Quote; sources[key] = "text"; }
        var missing = keys.Where(k => !values.ContainsKey(k)).ToList();
        if (missing.Count == 0) return new(values, quotes, sources, [], false);
        if (!settings.Enabled || settings.Model.Length == 0)
        {
            notes.Add("Turn on the AI reader in Settings to look for " + string.Join(", ", missing.Select(FieldLabel)) + ".");
            return new(values, quotes, sources, notes.ToArray(), false);
        }
        var budget = Math.Clamp(settings.MaxInputCharacters, 2000, FieldTextBudget);
        var excerpt = FieldExcerpt(entry.ExtractedText, missing.SelectMany(k => guides[k].Labels).Concat(missing.Select(FieldLabel)), budget);
        var prompt = JsonSerializer.Serialize(new
        {
            file_name = entry.Name, kind = kindName, kind_description = kind?.Description ?? "", from, date = date?.ToString("yyyy-MM-dd") ?? "",
            fields = missing.Select(k => new { name = k, type = guides[k].Type, look_for = guides[k].Look.Length > 0 ? guides[k].Look : FieldLabel(k), printed_as = guides[k].Labels }),
            text = excerpt
        });
        var reply = await AskReaderAsync(entry.Id, entry.ContentHash, settings.Revision, settings.TimeoutSeconds,
            token => WithReaderAsync(settings, client, (reader, seconds, ct) => client.FindFieldsAsync(new(reader.Endpoint, reader.Model, seconds, prompt, missing.ToArray(), settings.KeepAliveSeconds), ct), token), cancellationToken);
        foreach (var field in reply.Fields)
        {
            var key = missing.FirstOrDefault(k => k == field.Name.Trim().ToLowerInvariant().Replace(' ', '_'));
            if (key == null || values.ContainsKey(key)) continue;
            var value = SortPatterns.Clean(field.Value);
            if (!ValueFound(entry.ExtractedText, field.Value, guides[key]) && !ValueFound(entry.ExtractedText, value, guides[key]))
            { notes.Add($"Left {FieldLabel(key)} empty: the reader said “{Short(value)}”, which isn't in the text."); continue; }
            values[key] = NormalizeFieldValue(key, value, guides[key]); quotes[key] = field.Quote; sources[key] = "reader";
        }
        var notFound = missing.Where(k => !values.ContainsKey(k) && !notes.Any(n => n.Contains(FieldLabel(k) + " empty"))).ToList();
        if (notFound.Count > 0) notes.Add("Not found in this document: " + string.Join(", ", notFound.Select(FieldLabel)) + ".");
        return new(values, quotes, sources, notes.ToArray(), true);
    }
    private static string Short(string value) => value.Length > 40 ? value[..40] + "…" : value;

    // Set up fields with AI: from this document, the reader suggests which fields the kind should
    // carry and how to find each one. Nothing is saved until the person keeps what they want.
    public async Task<KindDraft> DraftKindAsync(Guid entryId, string kindName, string actor, IOllamaClient client, CancellationToken cancellationToken = default)
    {
        Actor(actor);
        kindName = KindName(kindName);
        var (entry, settings, kind) = await FieldReadingAsync(entryId, kindName);
        if (!settings.Enabled || settings.Model.Length == 0) throw new StorageException("Turn on the AI reader in Settings first.");
        var existing = kind == null ? new Dictionary<string, FieldGuide>() : FieldGuides(kind);
        var budget = Math.Clamp(settings.MaxInputCharacters, 2000, FieldTextBudget);
        var prompt = JsonSerializer.Serialize(new
        {
            file_name = entry.Name, kind = kind?.Name ?? kindName, kind_description = kind?.Description ?? "",
            existing_fields = existing.Select(x => new { name = FieldLabel(x.Key), type = x.Value.Type, look_for = x.Value.Look, labels = x.Value.Labels }),
            text = FieldExcerpt(entry.ExtractedText, existing.Values.SelectMany(x => x.Labels), budget)
        });
        var reply = await AskReaderAsync(entry.Id, entry.ContentHash, settings.Revision, settings.TimeoutSeconds,
            token => WithReaderAsync(settings, client, (reader, seconds, ct) => client.DraftKindAsync(new(reader.Endpoint, reader.Model, seconds, prompt, settings.KeepAliveSeconds), ct), token), cancellationToken);
        var drafted = new Dictionary<string, FieldDraft>();
        foreach (var field in reply.Fields)
        {
            string key;
            try { key = FieldKey(field.Name); } catch (StorageException) { continue; }
            key = existing.Keys.FirstOrDefault(k => k == key || FieldKey(FieldLabel(k)) == key) ?? key;
            if (ReservedAiFacts.Contains(key) || DetailKeys.Contains(key) || drafted.ContainsKey(key)) continue;
            var guide = CleanGuide(key, new(field.Type, field.LookFor, field.Labels));
            var value = SortPatterns.Clean(field.Value);
            var checkedValue = value.Length > 0 && (ValueFound(entry.ExtractedText, field.Value, guide) || ValueFound(entry.ExtractedText, value, guide)) ? NormalizeFieldValue(key, value, guide) : "";
            drafted[key] = new(key, guide, checkedValue, checkedValue.Length > 0 ? field.Quote : "", existing.ContainsKey(key));
        }
        // The kind's own fields stay first and are never dropped from the draft.
        // What a person already wrote about a field wins; the reader's suggestion fills what's empty.
        static FieldGuide Merge(FieldGuide own, FieldGuide suggested) => new(Described(own) ? own.Type : suggested.Type, own.Look.Length > 0 ? own.Look : suggested.Look,
            own.Labels.Concat(suggested.Labels).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray());
        var list = existing.Select(x => drafted.TryGetValue(x.Key, out var d) ? d with { Guide = Merge(x.Value, d.Guide) } : new(x.Key, x.Value, "", "", true))
            .Concat(drafted.Values.Where(x => !x.Existing)).Take(Math.Max(KindFieldLimit, existing.Count)).ToList();
        if (list.Count == 0) throw new StorageException("The reader didn't suggest any fields for this document.");
        return new(kind?.Name ?? kindName, reply.Description.Length > 0 ? reply.Description : kind?.Description ?? "", list, entry.ContentHash);
    }

    [GeneratedRegex(@"^(?<open>\()?\s*(?<neg>-)?\s*(?:[$€£]|USD\s?)?\s*(?<neg2>-)?\s*(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*(?<close>\))?\s*(?<sign>-|CR|DR)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MoneyPattern();
    [GeneratedRegex(@"\(?-?[$€£]?\s?-?\d[\d,]*(?:\.\d+)?\)?", RegexOptions.CultureInvariant)] private static partial Regex NumberToken();
    [GeneratedRegex(@"[A-Za-z0-9*#][A-Za-z0-9*\-]*\d[A-Za-z0-9*\-]*|\d", RegexOptions.CultureInvariant)] private static partial Regex IdToken();
    [GeneratedRegex(@"\s{3,}|\t", RegexOptions.CultureInvariant)] private static partial Regex ColumnGap();
    [GeneratedRegex(@"^[*•xX]{2,}[\s\-.]*(?<shown>[A-Za-z0-9]*\d[A-Za-z0-9\-]*)$", RegexOptions.CultureInvariant)] private static partial Regex MaskedId();
}
