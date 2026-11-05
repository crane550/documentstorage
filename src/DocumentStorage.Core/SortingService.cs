using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

// What a person approves: the details, never a path. The folder and name are worked out from
// the kind's patterns (or the person's own folder or name, when they change them).
public sealed record SortChoice(string Kind, string From, DateOnly? Date, string Title, string[] Tags, Dictionary<string, string> Fields,
    bool KeepName = false, string NameOverride = "", string FolderOverride = "", bool Remember = true,
    // This document's own arrangement of folders and name (from the arranger); null means the kind's.
    // KeepPatterns saves it to the kind when the document is approved.
    string? FolderPattern = null, string? NamePattern = null, bool KeepPatterns = false);
public sealed record SortPlan(string FolderPath, string[] NewFolders, string FileName, bool NewKind, bool NewSender, string[] NewTags, string? Problem)
{
    public string DestinationPath => FolderPath.Length == 0 ? FileName : FolderPath + "/" + FileName;
}
public sealed record SortDocument(Guid Id, Guid RootId, string RootName, string Name, string RelativePath, long Size, string TextStatus, string Readiness,
    string ContentHash, DateTimeOffset ObservedAt, bool Online, DateTimeOffset ModifiedAt);
// Sources say where each detail came from: "filename", "text", "reader", "person", "guess" or "".
public sealed record SortCard(SortDocument Document, SortChoice Choice, Dictionary<string, string> Sources, SortPlan Plan, bool Sure, string[] Notes,
    string ReaderState, Guid? JobId, string Confidence, string KindDescription, Dictionary<string, string> Quotes, bool DescriptiveName);
public sealed record SortInbox(List<SortCard> Cards, int Total);
public sealed record SortVocabulary(List<DocumentKind> Kinds, List<Correspondent> Senders, List<Tag> Tags,
    Dictionary<Guid, int> KindUse, Dictionary<Guid, int> SenderUse, Dictionary<Guid, int> TagUse);
public sealed record FiledItem(Guid PlanId, Guid EntryId, string FileName, string DestinationPath, string SourcePath, string Kind, string From,
    DateOnly? Date, string Title, string[] Tags, bool Automatic, bool Undone, bool Corrected, DateTimeOffset FiledAt, string Actor, bool Confirmed = false);
public sealed record DocumentDetailView(Guid EntryId, string Kind, string From, DateOnly? Date, string Title, string[] Tags, Dictionary<string, string> Fields, int Revision,
    bool FollowsFolder = false, bool FollowsName = false);

public sealed partial class LibraryService
{
    public const string SortSchema = "sort-v1";
    public const int SortTextBudget = 4000;
    public const int TrustStreak = 10;
    public const string DefaultKindFolder = "{kind}/{from}";
    public const string DefaultKindName = "{date} {from} {title}";
    private sealed record StoredSort(SortChoice Choice, string SuggestedKind, string SuggestedFrom, DateOnly? SuggestedDate, string RawFrom, string KindDescription, bool HadSuggestion);

    private static DocumentKind[] StarterKinds() =>
    [
        Kind("Bank statement", "A periodic statement for a bank, credit card, loan or investment account.", "Banking/{from}/{date:yyyy}", "{date:yyyy-MM} {from} statement {account_ending}", "account_ending"),
        Kind("Invoice", "A request for payment for goods or services, usually with an invoice number.", "Invoices/{from}/{date:yyyy}", "{date} {from} invoice {invoice_number}", "invoice_number", "total", "due_date"),
        Kind("Purchase order", "An order to buy goods or services, with a PO number.", "Purchase orders/{from}/{date:yyyy}", "{date} {from} PO {po_number}", "po_number", "total"),
        Kind("Bill", "A utility, phone, internet or other recurring bill.", "Bills/{from}/{date:yyyy}", "{date} {from} bill", "account_ending", "total", "due_date"),
        Kind("Receipt", "Proof of a purchase or payment already made.", "Receipts/{date:yyyy}", "{date} {from} receipt {total}", "total"),
        Kind("Medical", "Doctor, lab, hospital, dental, vision or pharmacy records, results and bills.", "Medical/{date:yyyy}", "{date} {from} {title}"),
        Kind("Vehicle", "Vehicle titles, registrations, repairs, inspections and insurance cards.", "Vehicles/{vehicle}", "{date} {from} {title}", "vehicle"),
        Kind("Tax document", "Tax forms, returns, notices and property tax statements.", "Taxes/{tax_year}", "{tax_year} {from} {title}", "tax_year"),
        Kind("Insurance", "Insurance policies, declarations, cards and claims.", "Insurance/{from}", "{date} {from} {title}", "policy_number"),
        Kind("Legal", "Contracts, agreements, court papers, deeds, wills and other legal documents.", "Legal", "{date} {title}"),
        Kind("Letter", "Correspondence and notices that fit no other kind.", "Letters/{date:yyyy}", "{date} {from} {title}"),
        Kind("School", "Homework, report cards, transcripts and school notices.", "School/{date:yyyy}", "{date} {title}"),
        new() { Name = "Everything else", Description = "Anything that fits no other kind: flyers, recipes, manuals, business cards, notes.", FolderPattern = "Everything else", NamePattern = "{date} {title}", IsCatchAll = true }
    ];
    private static DocumentKind Kind(string name, string description, string folder, string pattern, params string[] fields) =>
        new() { Name = name, Description = description, FolderPattern = folder, NamePattern = pattern, FieldsJson = JsonSerializer.Serialize(fields) };

    private async Task EnsureKindsAsync(CatalogDb db)
    {
        if (await db.Kinds.AnyAsync()) return;
        foreach (var kind in StarterKinds()) { kind.CreatedAt = time.GetUtcNow(); db.Kinds.Add(kind); }
        await db.SaveChangesAsync();
    }

    // ---------- Vocabulary: kinds, senders, tags ----------

    public async Task<SortVocabulary> SortVocabularyAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await EnsureKindsAsync(db);
        return new(await db.Kinds.AsNoTracking().OrderBy(x => x.IsCatchAll).ThenBy(x => x.Name).ToListAsync(),
            await db.Correspondents.AsNoTracking().OrderBy(x => x.Name).ToListAsync(),
            await db.Tags.AsNoTracking().OrderBy(x => x.Name).ToListAsync(),
            await db.Details.Where(x => x.KindId != null).GroupBy(x => x.KindId!.Value).ToDictionaryAsync(x => x.Key, x => x.Count()),
            await db.Details.Where(x => x.CorrespondentId != null).GroupBy(x => x.CorrespondentId!.Value).ToDictionaryAsync(x => x.Key, x => x.Count()),
            await db.EntryTags.GroupBy(x => x.TagId).ToDictionaryAsync(x => x.Key, x => x.Count()));
    }

    public static string[] KindFields(DocumentKind kind)
    {
        try { return JsonSerializer.Deserialize<string[]>(kind.FieldsJson) ?? []; } catch (JsonException) { return []; }
    }
    public static string[] Aliases(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; } catch (JsonException) { return []; }
    }

    private static string KindName(string name)
    {
        name = SortPatterns.Clean(name);
        if (name.Length is 0 or > 60) throw new StorageException("Give the kind a name of up to 60 characters.");
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
    private static string SenderName(string name)
    {
        name = SortPatterns.Clean(name);
        if (name.Length is 0 or > 80) throw new StorageException("Give the sender a name of up to 80 characters.");
        return name;
    }
    public static string TagName(string name)
    {
        name = SortPatterns.Clean(name.Trim().TrimStart('#')).ToLowerInvariant().Replace(' ', '-');
        while (name.Contains("--")) name = name.Replace("--", "-");
        if (name.Length is 0 or > 40) throw new StorageException("Use a tag of up to 40 characters.");
        return name;
    }
    private static string[] CleanAliases(IEnumerable<string> aliases, string name) => aliases.Select(x => string.Join(' ', new string(x.Where(c => !char.IsControl(c)).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries))).Where(x => x.Length is > 0 and <= 80)
        .Where(x => !string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();

    public async Task<DocumentKind> SaveKindAsync(DocumentKind input, string actor)
    {
        Actor(actor);
        var name = KindName(input.Name);
        var folder = SortPatterns.ValidatePattern(input.FolderPattern, folder: true);
        var pattern = SortPatterns.ValidatePattern(input.NamePattern, folder: false);
        var fields = KindFields(input).Select(FactKey).Distinct(StringComparer.Ordinal).Take(12).ToArray();
        if (input.Description.Length > 400) throw new StorageException("Keep the description under 400 characters.");
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var kind = input.Id == Guid.Empty ? null : await db.Kinds.SingleOrDefaultAsync(x => x.Id == input.Id);
            if (kind != null && kind.Revision != input.Revision) throw new StorageException("This kind changed in another tab. Reload it before saving.");
            var clash = await db.Kinds.AnyAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != input.Id);
            if (clash) throw new StorageException("There is already a kind called " + name + ".");
            if (kind == null) { kind = new() { CreatedAt = time.GetUtcNow(), Revision = 0 }; db.Kinds.Add(kind); }
            kind.Name = name; kind.Description = input.Description.Trim(); kind.FolderPattern = folder; kind.NamePattern = pattern;
            kind.FieldsJson = JsonSerializer.Serialize(fields); kind.AutoFile = input.AutoFile; kind.Revision++;
            if (input.IsCatchAll && !kind.IsCatchAll)
            {
                foreach (var other in await db.Kinds.Where(x => x.IsCatchAll && x.Id != kind.Id).ToListAsync()) other.IsCatchAll = false;
                kind.IsCatchAll = true;
            }
            await db.SaveChangesAsync();
            return kind;
        }
        finally { writer.Release(); }
    }

    public async Task SetKindAutoFileAsync(Guid kindId, bool on, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var kind = await db.Kinds.SingleOrDefaultAsync(x => x.Id == kindId) ?? throw new StorageException("That kind no longer exists.");
            kind.AutoFile = on; kind.Revision++;
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task SetAutoFilingEnabledAsync(bool on, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var settings = await AiDefaultsAsync(db);
            settings.AutoFileEnabled = on;
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<bool> AutoFilingEnabledAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.OllamaConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1))?.AutoFileEnabled ?? true;
    }

    // Deleting a kind leaves its documents where they are, without a kind.
    public async Task DeleteKindAsync(Guid kindId, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var kind = await db.Kinds.SingleOrDefaultAsync(x => x.Id == kindId) ?? throw new StorageException("That kind no longer exists.");
            if (kind.IsCatchAll) throw new StorageException("Make another kind the catch-all before deleting this one.");
            await db.Details.Where(x => x.KindId == kindId).ExecuteUpdateAsync(set => set.SetProperty(x => x.KindId, (Guid?)null));
            db.Kinds.Remove(kind);
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<Correspondent> SaveSenderAsync(Guid id, string name, IEnumerable<string> aliases, string actor)
    {
        Actor(actor); name = SenderName(name);
        var clean = CleanAliases(aliases, name);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var sender = id == Guid.Empty ? null : await db.Correspondents.SingleOrDefaultAsync(x => x.Id == id);
            if (await db.Correspondents.AnyAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != id)) throw new StorageException("There is already a sender called " + name + ". Merge them instead.");
            if (sender == null) { sender = new() { CreatedAt = time.GetUtcNow() }; db.Correspondents.Add(sender); }
            else sender.Revision++;
            sender.Name = name; sender.AliasesJson = JsonSerializer.Serialize(clean);
            await db.SaveChangesAsync();
            return sender;
        }
        finally { writer.Release(); }
    }

    // Merging keeps the target's name; the other's name and spellings become the target's.
    public async Task MergeSendersAsync(Guid fromId, Guid intoId, string actor)
    {
        Actor(actor);
        if (fromId == intoId) return;
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var from = await db.Correspondents.SingleAsync(x => x.Id == fromId); var into = await db.Correspondents.SingleAsync(x => x.Id == intoId);
            into.AliasesJson = JsonSerializer.Serialize(CleanAliases(Aliases(into.AliasesJson).Append(from.Name).Concat(Aliases(from.AliasesJson)), into.Name)); into.Revision++;
            await db.Details.Where(x => x.CorrespondentId == fromId).ExecuteUpdateAsync(set => set.SetProperty(x => x.CorrespondentId, (Guid?)intoId));
            await MergeRoutesAsync(db, fromId, intoId);
            db.Correspondents.Remove(from);
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task DeleteSenderAsync(Guid id, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Details.Where(x => x.CorrespondentId == id).ExecuteUpdateAsync(set => set.SetProperty(x => x.CorrespondentId, (Guid?)null));
            await db.Correspondents.Where(x => x.Id == id).ExecuteDeleteAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<Tag> SaveTagAsync(Guid id, string name, IEnumerable<string> aliases, string actor)
    {
        Actor(actor); name = TagName(name);
        var clean = aliases.Select(x => { try { return TagName(x); } catch (StorageException) { return ""; } }).Where(x => x.Length > 0 && x != name).Distinct().Take(30).ToArray();
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var tag = id == Guid.Empty ? null : await db.Tags.SingleOrDefaultAsync(x => x.Id == id);
            if (await db.Tags.AnyAsync(x => x.Name == name && x.Id != id)) throw new StorageException("There is already a tag called " + name + ". Merge them instead.");
            if (tag == null) { tag = new() { CreatedAt = time.GetUtcNow() }; db.Tags.Add(tag); }
            else tag.Revision++;
            tag.Name = name; tag.AliasesJson = JsonSerializer.Serialize(clean);
            await db.SaveChangesAsync();
            return tag;
        }
        finally { writer.Release(); }
    }

    public async Task MergeTagsAsync(Guid fromId, Guid intoId, string actor)
    {
        Actor(actor);
        if (fromId == intoId) return;
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var from = await db.Tags.SingleAsync(x => x.Id == fromId); var into = await db.Tags.SingleAsync(x => x.Id == intoId);
            into.AliasesJson = JsonSerializer.Serialize(Aliases(into.AliasesJson).Append(from.Name).Concat(Aliases(from.AliasesJson)).Where(x => x != into.Name).Distinct().Take(30)); into.Revision++;
            var already = await db.EntryTags.Where(x => x.TagId == intoId).Select(x => x.EntryId).ToListAsync();
            await db.EntryTags.Where(x => x.TagId == fromId && already.Contains(x.EntryId)).ExecuteDeleteAsync();
            await db.EntryTags.Where(x => x.TagId == fromId).ExecuteUpdateAsync(set => set.SetProperty(x => x.TagId, intoId));
            db.Tags.Remove(from);
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    public async Task DeleteTagAsync(Guid id, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Tags.Where(x => x.Id == id).ExecuteDeleteAsync();
        }
        finally { writer.Release(); }
    }

    // ---------- Matching what the reader or a person typed to what Stash knows ----------

    public static DocumentKind? MatchKind(string text, IEnumerable<DocumentKind> kinds)
    {
        var wanted = DocumentHints.Normalize(text);
        if (wanted.Length == 0) return null;
        string Single(string x) => x.EndsWith("s", StringComparison.Ordinal) && !x.EndsWith("ss", StringComparison.Ordinal) ? x[..^1] : x;
        return kinds.FirstOrDefault(x => DocumentHints.Normalize(x.Name) == wanted) ?? kinds.FirstOrDefault(x => Single(DocumentHints.Normalize(x.Name)) == Single(wanted));
    }

    // Exact name or spelling first; then one name starting the other on a word boundary
    // ("Cedar Bank" for "Cedar Bank, N.A."), when the shorter is long enough to mean something.
    public static T? MatchNamed<T>(string text, IEnumerable<T> items, Func<T, string> name, Func<T, string[]> aliases) where T : class
    {
        var wanted = DocumentHints.Normalize(text);
        if (wanted.Length == 0) return null;
        var list = items.ToList();
        var exact = list.FirstOrDefault(x => DocumentHints.Normalize(name(x)) == wanted || aliases(x).Any(a => DocumentHints.Normalize(a) == wanted));
        if (exact != null) return exact;
        return list.Select(x => (Item: x, Score: new[] { name(x) }.Concat(aliases(x)).Select(DocumentHints.Normalize).Where(n => n.Length >= 4)
                .Select(n => wanted.StartsWith(n + " ", StringComparison.Ordinal) || n.StartsWith(wanted + " ", StringComparison.Ordinal) && wanted.Length >= 4 ? n.Length : 0).DefaultIfEmpty(0).Max()))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Item).FirstOrDefault();
    }

    private static bool QuoteFound(string text, string quote)
    {
        static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var q = Squash(quote);
        return q.Length >= 2 && Squash(text).Contains(q, StringComparison.Ordinal);
    }

    // ---------- Suggestions ----------

    private sealed record Vocab(List<DocumentKind> Kinds, List<Correspondent> Senders, List<Tag> Tags)
    {
        public DocumentKind? KindNamed(string name) => MatchKind(name, Kinds);
        public Correspondent? Sender(string name) => MatchNamed(name, Senders, x => x.Name, x => Aliases(x.AliasesJson));
        public Tag? TagNamed(string name) => MatchNamed(name, Tags, x => x.Name, x => Aliases(x.AliasesJson));
    }
    private static async Task<Vocab> VocabAsync(CatalogDb db) => new(
        await db.Kinds.AsNoTracking().OrderBy(x => x.Name).ToListAsync(), await db.Correspondents.AsNoTracking().OrderBy(x => x.Name).ToListAsync(), await db.Tags.AsNoTracking().OrderBy(x => x.Name).ToListAsync());

    private static DocumentHintSet Hints(CatalogEntry entry, Vocab vocab) => DocumentHints.Read(entry.Name, entry.ExtractedText,
        vocab.Kinds.Select(x => new KindTerm(x.Id, x.Name, x.Description, KindFields(x), x.IsCatchAll)).ToList(),
        vocab.Senders.Select(x => new NamedTerm(x.Id, x.Name, Aliases(x.AliasesJson))).ToList(),
        vocab.Tags.Select(x => new NamedTerm(x.Id, x.Name, Aliases(x.AliasesJson))).ToList());

    // From the filename and text alone: used when the reader is off or hasn't read it yet.
    private static (SortChoice Choice, Dictionary<string, string> Sources) FromHints(CatalogEntry entry, Vocab vocab, DocumentHintSet hints, DocumentDescription? description = null)
    {
        var sources = new Dictionary<string, string>();
        var kind = hints.Kinds.FirstOrDefault(x => x.Score >= 10) is var top && top.Id != Guid.Empty ? vocab.Kinds.First(x => x.Id == top.Id) : null;
        if (kind != null) sources["kind"] = "filename";
        var sender = hints.Senders.Count > 0 ? vocab.Senders.First(x => x.Id == hints.Senders[0]) : null;
        if (sender != null) sources["from"] = "text";
        var date = hints.Dates.FirstOrDefault(x => x.Where == "filename" && hints.DescriptiveName) ?? hints.Dates.FirstOrDefault(x => x.Where == "text");
        if (date != null) sources["date"] = date.Where == "filename" ? "filename" : "guess";
        var title = hints.DescriptiveName ? SortPatterns.Clean(string.Join(' ', hints.NameWords.Where(w => !w.All(char.IsDigit)))) : "";
        if (title.Length > 0) sources["title"] = "filename";
        var tags = hints.Tags.Take(4).Select(id => vocab.Tags.First(x => x.Id == id).Name).ToArray();
        if (tags.Length > 0) sources["tags"] = "text";
        // An older reading's saved description (title, tags, organizations) fills what plain matching didn't.
        var from = sender?.Name ?? "";
        if (description != null)
        {
            if (title.Length == 0 && description.Title.Trim().Length > 0) { title = SortPatterns.Clean(description.Title); sources["title"] = "reader"; }
            if (from.Length == 0 && description.Organizations.FirstOrDefault() is { } organization)
            { from = vocab.Sender(organization)?.Name ?? SafeSender(organization); if (from.Length > 0) sources["from"] = vocab.Sender(organization) != null ? "reader" : "guess"; }
            var more = description.Tags.Select(x => vocab.TagNamed(x)?.Name ?? (TryTag(x) ?? "")).Where(x => x.Length > 0);
            var merged = tags.Concat(more).Distinct().Take(6).ToArray();
            if (merged.Length > tags.Length) { tags = merged; sources["tags"] = "reader"; }
        }
        return (new(kind?.Name ?? "", from, date?.Date, title.Length > 80 ? title[..80] : title, tags, new(), KeepName: hints.DescriptiveName), sources);
    }

    // The reader's answer, checked: kind, sender and tags mapped to what Stash knows, quotes
    // checked against the text, and a date only trusted when it's written in the document.
    private static (SortChoice Choice, Dictionary<string, string> Sources, Dictionary<string, string> Quotes, List<string> Notes, bool Sure)
        FromReply(CatalogEntry entry, Vocab vocab, DocumentHintSet hints, SortReply reply)
    {
        var sources = new Dictionary<string, string>(); var notes = new List<string>(); var quotes = new Dictionary<string, string>();
        var kind = vocab.KindNamed(reply.Kind);
        var kindName = kind?.Name ?? SafeKindName(reply.Kind);
        sources["kind"] = "reader";
        if (kind == null) notes.Add("New kind: " + kindName);
        var sender = vocab.Sender(reply.From);
        var fromName = sender?.Name ?? (reply.From.Length > 0 ? SafeSender(reply.From) : "");
        if (fromName.Length > 0) sources["from"] = sender != null ? "reader" : "guess";
        if (sender == null && fromName.Length > 0) notes.Add("New sender: " + fromName);
        DateOnly? date = DateOnly.TryParseExact(reply.Date, "yyyy-MM-dd", out var parsed) ? parsed : null;
        if (date is DateOnly d && !DocumentHints.Mentions(entry.Name, entry.ExtractedText, d)) { sources["date"] = "guess"; notes.Add("The date isn't written that way in the document"); }
        else if (date != null) sources["date"] = "reader";
        var title = SortPatterns.Clean(reply.Title); if (title.Length > 80) title = title[..80];
        if (title.Length > 0) sources["title"] = "reader";
        var tags = new List<string>(); var newTags = 0;
        foreach (var raw in reply.Tags)
        {
            var known = vocab.TagNamed(raw);
            if (known != null) { if (!tags.Contains(known.Name)) tags.Add(known.Name); continue; }
            string name; try { name = TagName(raw); } catch (StorageException) { continue; }
            if (newTags++ == 0 && !tags.Contains(name)) { tags.Add(name); notes.Add("New tag: " + name); }
        }
        foreach (var id in hints.Tags) { var name = vocab.Tags.First(x => x.Id == id).Name; if (!tags.Contains(name) && tags.Count < 5) tags.Add(name); }
        if (tags.Count > 0) sources["tags"] = "reader";
        var allowed = kind != null ? KindFields(kind).ToHashSet() : null;
        var fields = new Dictionary<string, string>();
        foreach (var field in reply.Fields)
        {
            string key; try { key = FactKey(field.Name.Trim().Replace(' ', '_').Replace('-', '_')); } catch (StorageException) { continue; }
            if (allowed != null && !allowed.Contains(key)) continue;
            if (!QuoteFound(entry.ExtractedText, field.Quote) && !QuoteFound(entry.ExtractedText, field.Value)) continue;
            fields[key] = NormalizeFieldValue(key, SortPatterns.Clean(field.Value)); quotes[key] = field.Quote;
        }
        var patterns = (kind?.FolderPattern ?? DefaultKindFolder) + "/" + (kind?.NamePattern ?? DefaultKindName);
        var tokens = SortPatterns.TokensIn(patterns);
        var sure = kind != null && reply.Confidence == "high" && newTags == 0
            && (!tokens.Contains("from") || sender != null)
            && (!tokens.Any(t => t == "date") || sources.GetValueOrDefault("date") == "reader")
            && (kind == null || KindFields(kind).Where(tokens.Contains).All(fields.ContainsKey));
        if (reply.Confidence != "high") notes.Add("The reader wasn't sure (" + reply.Confidence + ")");
        return (new(kindName, fromName, date, title, tags.ToArray(), fields, KeepName: hints.DescriptiveName), sources, quotes, notes, sure);
    }

    private static string? TryTag(string text) { try { return TagName(text); } catch (StorageException) { return null; } }
    private static string SafeKindName(string text) { try { return KindName(text); } catch (StorageException) { return ""; } }
    private static string SafeSender(string text) { try { return SenderName(text); } catch (StorageException) { return ""; } }

    private static SortReply? StoredReply(IdentificationJob? job)
    {
        if (job == null || !job.Sorting || job.State is not ("Succeeded" or "Filed") || job.OrganizationJson.Length == 0) return null;
        try { return JsonSerializer.Deserialize<SortReply>(job.OrganizationJson); } catch (JsonException) { return null; }
    }

    private static string ReaderState(IdentificationJob? job, OllamaSettings? settings, CatalogEntry entry)
    {
        if (job == null) return settings?.Enabled == true ? (entry.TextStatus == "Indexed" ? "Not read yet" : "No text to read") : "Reader off";
        return job.State switch
        {
            "Queued" => "Waiting to be read", "Retry" => "Will try again", "Running" => "Reading",
            "Succeeded" or "Filed" => "Read", "Failed" => "Reading failed: " + job.LastError, "Cancelled" => "Reading cancelled",
            "Superseded" => "Needs reading again", "Rejected" => "Declined", _ => job.State
        };
    }

    private async Task<SortCard> CardAsync(CatalogDb db, CatalogEntry entry, StorageRoot root, Vocab vocab, IdentificationJob? job, OllamaSettings? settings, List<CatalogEntry> folders)
    {
        var hints = Hints(entry, vocab);
        var doc = new SortDocument(entry.Id, entry.RootId, root.Name, entry.Name, entry.RelativePath, entry.Size, entry.TextStatus, entry.Readiness, entry.ContentHash, entry.ObservedAt, root.IsOnline, entry.ModifiedAt);
        var reply = job != null && job.ContentHash == entry.ContentHash ? StoredReply(job) : null;
        SortChoice choice; Dictionary<string, string> sources; var quotes = new Dictionary<string, string>(); var notes = new List<string>(); var sure = false;
        var saved = await db.Details.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entry.Id);
        if (saved != null)
        {
            // Details a person already gave (an undone filing, or set in the Library) win.
            var view = await DetailViewAsync(db, saved);
            choice = new(view.Kind, view.From, view.Date, view.Title, view.Tags, view.Fields, KeepName: hints.DescriptiveName);
            sources = new() { ["kind"] = "person", ["from"] = "person", ["date"] = "person", ["title"] = "person", ["tags"] = "person" };
        }
        // A regular's memory, when it's sure, beats a reading: it is what the person chose last time.
        else if (FromMemory(entry, vocab, hints, await RoutesForAsync(db, hints.Senders.Take(1))) is { } memory && (memory.Sure || reply == null))
        { choice = memory.Choice; sources = memory.Sources; notes = memory.Notes; sure = memory.Sure; }
        else if (reply != null) (choice, sources, quotes, notes, sure) = FromReply(entry, vocab, hints, reply);
        else
        {
            var description = await db.DocumentDescriptions.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entry.Id && x.ContentHash == entry.ContentHash);
            (choice, sources) = FromHints(entry, vocab, hints, description);
        }
        var plan = await PlanAsync(db, root, entry, choice, vocab, folders);
        if (plan.Problem != null) sure = false;
        return new(doc, choice, sources, plan, sure, notes.ToArray(), ReaderState(job, settings, entry), job?.Id, reply?.Confidence ?? "", reply?.KindDescription ?? "", quotes, hints.DescriptiveName);
    }

    private static async Task<IdentificationJob?> LatestSortJobAsync(CatalogDb db, Guid entryId) =>
        await db.IdentificationJobs.AsNoTracking().Where(x => x.EntryId == entryId && x.Sorting).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();

    private static Task<List<CatalogEntry>> FoldersAsync(CatalogDb db, Guid rootId) =>
        db.Entries.AsNoTracking().Where(x => x.RootId == rootId && x.IsFolder && !x.IsMissing && x.TrashBatchId == null)
            .Select(x => new CatalogEntry { Id = x.Id, RootId = x.RootId, ParentId = x.ParentId, Name = x.Name, RelativePath = x.RelativePath, IsFolder = true, IsIntake = x.IsIntake }).ToListAsync();

    public async Task<SortInbox> SortInboxAsync(int offset = 0, int take = 50)
    {
        await using var db = await factory.CreateDbContextAsync();
        await EnsureKindsAsync(db);
        var query = (await IntakeQueryAsync(db)).AsNoTracking();
        var total = await query.CountAsync();
        var entries = await query.OrderBy(x => x.ObservedAt).ThenBy(x => x.Name).ThenBy(x => x.Id).Skip(Math.Max(0, offset)).Take(Math.Clamp(take, 1, 100)).ToListAsync();
        var vocab = await VocabAsync(db);
        var settings = await db.OllamaConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
        var roots = await db.Roots.AsNoTracking().ToDictionaryAsync(x => x.Id);
        var folders = new Dictionary<Guid, List<CatalogEntry>>();
        var cards = new List<SortCard>();
        foreach (var entry in entries)
        {
            if (!folders.TryGetValue(entry.RootId, out var list)) folders[entry.RootId] = list = await FoldersAsync(db, entry.RootId);
            cards.Add(await CardAsync(db, entry, roots[entry.RootId], vocab, await LatestSortJobAsync(db, entry.Id), settings, list));
        }
        return new(cards, total);
    }

    public async Task<SortCard?> SortCardAsync(Guid entryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await EnsureKindsAsync(db);
        var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId);
        if (entry == null || entry.IsFolder) return null;
        var root = await db.Roots.AsNoTracking().SingleAsync(x => x.Id == entry.RootId);
        var settings = await db.OllamaConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
        return await CardAsync(db, entry, root, await VocabAsync(db), await LatestSortJobAsync(db, entry.Id), settings, await FoldersAsync(db, entry.RootId));
    }

    // Where a choice would put the document, worked out fresh. Nothing is written.
    public async Task<SortPlan> PlanSortAsync(Guid entryId, SortChoice choice)
    {
        await using var db = await factory.CreateDbContextAsync();
        var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId) ?? throw new StorageException("That document is no longer in the catalog.");
        var root = await db.Roots.AsNoTracking().SingleAsync(x => x.Id == entry.RootId);
        return await PlanAsync(db, root, entry, choice, await VocabAsync(db), await FoldersAsync(db, entry.RootId));
    }

    private async Task<SortPlan> PlanAsync(CatalogDb db, StorageRoot root, CatalogEntry entry, SortChoice choice, Vocab vocab, List<CatalogEntry> folders, string[]? fixedSegments = null)
    {
        var kind = vocab.KindNamed(choice.Kind);
        var sender = vocab.Sender(choice.From);
        var newTags = choice.Tags.Where(x => vocab.TagNamed(x) == null).ToArray();
        string kindName; string fromName;
        try
        {
            kindName = kind?.Name ?? (choice.Kind.Trim().Length == 0 ? "" : KindName(choice.Kind));
            fromName = sender?.Name ?? (choice.From.Trim().Length == 0 ? "" : SenderName(choice.From));
        }
        catch (StorageException ex) { return new("", [], entry.Name, kind == null, sender == null, newTags, ex.Message); }
        if (kindName.Length == 0) return new("", [], entry.Name, false, false, newTags, "Choose what kind of document this is.");
        var values = new SortValues(kindName, fromName, choice.Date, SortPatterns.Clean(choice.Title), choice.Fields);
        string folderPattern, namePattern;
        try
        {
            folderPattern = choice.FolderPattern != null ? SortPatterns.ValidatePattern(choice.FolderPattern, folder: true) : kind?.FolderPattern ?? DefaultKindFolder;
            namePattern = choice.NamePattern != null ? SortPatterns.ValidatePattern(choice.NamePattern, folder: false) : kind?.NamePattern ?? DefaultKindName;
        }
        catch (StorageException ex) { return new("", [], entry.Name, kind == null, sender == null && fromName.Length > 0, newTags, ex.Message); }
        string[] segments;
        try
        {
            segments = fixedSegments ?? (choice.FolderOverride.Trim().Length > 0
                ? choice.FolderOverride.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => StoragePaths.ValidateName(x.Trim())).ToArray()
                : SortPatterns.Folder(folderPattern, values));
        }
        catch (StorageException ex) { return new("", [], entry.Name, kind == null, sender == null && fromName.Length > 0, newTags, ex.Message); }
        // Walk down from the storage root, reusing folders that exist (any letter case).
        var byParent = folders.ToLookup(x => x.ParentId);
        var current = folders.SingleOrDefault(x => x.RelativePath == "");
        if (current == null) return new("", [], entry.Name, kind == null, sender == null, newTags, "The storage location's root folder is missing. Rescan.");
        var path = ""; var created = new List<string>();
        var byId = folders.ToDictionary(x => x.Id);
        foreach (var segment in segments)
        {
            if (created.Count == 0)
            {
                var children = byParent[current.Id].ToList();
                var next = children.FirstOrDefault(x => x.Name == segment) ?? children.FirstOrDefault(x => string.Equals(x.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (next != null)
                {
                    current = next; path = next.RelativePath;
                    if (HasIntakeAncestor(next, byId)) return new(path, [], entry.Name, kind == null, sender == null, newTags, "That folder is an inbox. Choose a folder outside the inbox.");
                    continue;
                }
                if (StoragePaths.Exists(StoragePaths.Resolve(root.Location, StoragePaths.Join(path, segment))) || await db.Entries.AnyAsync(x => x.RootId == root.Id && x.RelativePath == StoragePaths.Join(path, segment)))
                    return new(StoragePaths.Join(path, segment), [], entry.Name, kind == null, sender == null, newTags, "Something not yet in the catalog is already at " + StoragePaths.Join(path, segment) + ". Rescan storage.");
            }
            created.Add(segment); path = StoragePaths.Join(path, segment);
        }
        if (created.Count == 0 && HasIntakeAncestor(current, byId)) return new(path, [], entry.Name, kind == null, sender == null, newTags, "That folder is an inbox. Choose a folder outside the inbox.");
        string name;
        try
        {
            if (choice.NameOverride.Trim().Length > 0)
            {
                name = StoragePaths.ValidateName(choice.NameOverride.Trim());
                if (!string.Equals(Path.GetExtension(name), Path.GetExtension(entry.Name), StringComparison.OrdinalIgnoreCase)) name += Path.GetExtension(entry.Name);
                name = StoragePaths.ValidateName(name);
            }
            else name = choice.KeepName ? entry.Name : SortPatterns.Name(namePattern, values, entry.Name);
        }
        catch (StorageException ex) { return new(path, created.ToArray(), entry.Name, kind == null, sender == null && fromName.Length > 0, newTags, ex.Message); }
        // Never overwrite: a name already taken gets " (2)", " (3)"…
        if (created.Count == 0)
        {
            var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
            for (var i = 2; i <= 100; i++)
            {
                var target = StoragePaths.Join(path, name);
                // The document's own current path is free for itself (re-filing a document already in place).
                if (target == entry.RelativePath || (!StoragePaths.Exists(StoragePaths.Resolve(root.Location, target)) && !await db.Entries.AnyAsync(x => x.RootId == root.Id && x.RelativePath == target))) break;
                if (i == 100) return new(path, [], name, kind == null, sender == null, newTags, "Too many documents already have this name here. Change the name.");
                name = StoragePaths.ValidateName($"{stem} ({i}){extension}");
            }
        }
        return new(path, created.ToArray(), name, kind == null, sender == null && fromName.Length > 0, newTags, null);
    }

    // ---------- Reading (the model) ----------

    public Task<Guid> QueueSortingAsync(Guid entryId, string actor, bool retry = true) => QueueIdentificationAsync(entryId, actor, retry, sorting: true);

    private static readonly string SortContextHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SortSchema)));

    // The prompt: the filename, short lists of likely kinds, senders and tags, a few approved
    // examples, and the first page or so. Folder changes don't touch it, so they never throw
    // away a slow reading. Approval works out the path fresh anyway.
    private async Task<IdentificationContext> SortingContextAsync(CatalogDb db, CatalogEntry entry, OllamaSettings settings)
    {
        await EnsureKindsAsync(db);
        var vocab = await VocabAsync(db);
        var hints = Hints(entry, vocab);
        var kindRank = hints.Kinds.Select((x, i) => (x.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var kindUse = await db.Details.Where(x => x.KindId != null).GroupBy(x => x.KindId!.Value).ToDictionaryAsync(x => x.Key, x => x.Count());
        var kinds = vocab.Kinds.OrderBy(x => kindRank.GetValueOrDefault(x.Id, int.MaxValue)).ThenByDescending(x => kindUse.GetValueOrDefault(x.Id)).ThenBy(x => x.Name).ToList();
        if (kinds.Count > 20) kinds = kinds.Where(x => !x.IsCatchAll).Take(19).Concat(kinds.Where(x => x.IsCatchAll)).ToList();
        var senderUse = await db.Details.Where(x => x.CorrespondentId != null).GroupBy(x => x.CorrespondentId!.Value).ToDictionaryAsync(x => x.Key, x => x.Count());
        var senders = hints.Senders.Select(id => vocab.Senders.First(x => x.Id == id))
            .Concat(vocab.Senders.OrderByDescending(x => senderUse.GetValueOrDefault(x.Id)).ThenBy(x => x.Name)).DistinctBy(x => x.Id).Take(25).Select(x => x.Name).ToArray();
        var tagUse = await db.EntryTags.GroupBy(x => x.TagId).ToDictionaryAsync(x => x.Key, x => x.Count());
        var tags = hints.Tags.Select(id => vocab.Tags.First(x => x.Id == id))
            .Concat(vocab.Tags.OrderByDescending(x => tagUse.GetValueOrDefault(x.Id)).ThenBy(x => x.Name)).DistinctBy(x => x.Id).Take(30).Select(x => x.Name).ToArray();
        var words = DocumentHints.Words(entry.Name + " " + (entry.ExtractedText.Length > 3000 ? entry.ExtractedText[..3000] : entry.ExtractedText)).Select(x => x.ToLowerInvariant()).Where(x => x.Length >= 4).ToHashSet();
        var examples = (await db.FilingExamples.AsNoTracking().Where(x => x.RemovedAt == null).OrderByDescending(x => x.CreatedAt).Take(150).ToListAsync())
            .OrderByDescending(x => DocumentHints.Words(x.OriginalName + " " + x.DocumentType + " " + x.Summary).Select(w => w.ToLowerInvariant()).Count(words.Contains)).ThenByDescending(x => x.CreatedAt)
            .Take(3).Select(x => new { file_name = x.OriginalName, kind = x.DocumentType, title = x.Summary.Length > 80 ? x.Summary[..80] : x.Summary, filed_as = x.Filename }).ToArray();
        var budget = Math.Min(SortTextBudget, settings.MaxInputCharacters);
        var text = entry.ExtractedText.Length > budget ? entry.ExtractedText[..budget] + "\n[the rest of the document is not shown]" : entry.ExtractedText;
        var json = JsonSerializer.Serialize(new
        {
            file_name = entry.Name,
            known_kinds = kinds.Select(x => new { name = x.Name, description = x.Description.Length > 160 ? x.Description[..160] : x.Description, fields = KindFields(x) }),
            known_senders = senders, known_tags = tags, approved_examples = examples,
            general_guidance = settings.Instructions.Length > 800 ? settings.Instructions[..800] : settings.Instructions,
            text
        });
        return new(json, SortContextHash, [], [], entry.ExtractedText.Length > budget, Sorting: true, InputText: text);
    }

    // ---------- Filing ----------

    private static string Key(string text) => DocumentHints.Normalize(text);

    public Task<Guid> FileSortedAsync(Guid entryId, SortChoice choice, string expectedContentHash, string actor) =>
        FileSortedCoreAsync(entryId, choice, expectedContentHash, actor, automatic: false);

    private async Task<Guid> FileSortedCoreAsync(Guid entryId, SortChoice choice, string expectedContentHash, string actor, bool automatic)
    {
        Actor(actor);
        if (choice.Title.Length > 200 || choice.Tags.Length > 12 || choice.Fields.Count > 16 || choice.Fields.Values.Any(x => x.Length > 300))
            throw new StorageException("Keep the title short, with up to 12 tags and 16 fields.");
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await EnsureKindsAsync(db);
            var (entry, root) = await WritableEntryAsync(db, entryId);
            await RequireIntakeAsync(db, entry); RequireStable(entry);
            if (entry.ContentHash != expectedContentHash || await FingerprintAsync(root, entry) != expectedContentHash)
                throw new StorageException("The document changed since you looked at it. Check it again before filing.");
            var vocab = await VocabAsync(db);
            var folders = await FoldersAsync(db, root.Id);
            var plan = await PlanAsync(db, root, entry, choice, vocab, folders);
            if (plan.Problem != null) throw new StorageException(plan.Problem);
            var job = await db.IdentificationJobs.AsNoTracking().Where(x => x.EntryId == entry.Id && x.Sorting && x.ContentHash == entry.ContentHash && x.State == "Succeeded")
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
            var reply = StoredReply(job);
            // What the card suggested: a regular's memory when it was sure (or there was no reading), else the reading.
            var hints = Hints(entry, vocab);
            var memory = FromMemory(entry, vocab, hints, await RoutesForAsync(db, hints.Senders.Take(1)));
            var fromMemory = memory != null && (memory.Sure || reply == null);
            var suggested = fromMemory ? memory!.Choice : reply == null ? null : FromReply(entry, vocab, hints, reply).Choice;
            var stored = new StoredSort(choice, suggested?.Kind ?? "", suggested?.From ?? "", suggested?.Date, fromMemory ? "" : reply?.From ?? "", reply?.KindDescription ?? "", suggested != null);
            var parentPath = plan.NewFolders.Length == 0 ? plan.FolderPath : string.Join('/', plan.FolderPath.Split('/')[..^plan.NewFolders.Length]);
            var parent = folders.Single(x => x.RelativePath == parentPath);
            var filing = new OrganizationFiling
            {
                JobId = job?.Id, EntryId = entry.Id, Actor = actor, SourcePath = entry.RelativePath, ContentHash = entry.ContentHash, ContextHash = SortContextHash,
                ParentFolderId = parent.Id, ParentPath = parent.RelativePath, NewFolderName = string.Join('/', plan.NewFolders),
                TargetFolderId = plan.NewFolders.Length == 0 ? parent.Id : Guid.NewGuid(), Filename = plan.FileName, DestinationPath = plan.DestinationPath,
                DocumentType = vocab.KindNamed(choice.Kind)?.Name ?? KindName(choice.Kind), FactsJson = JsonSerializer.Serialize(choice.Fields), Reason = choice.Title,
                Remember = choice.Remember, Automatic = automatic, DetailsJson = JsonSerializer.Serialize(stored),
                CreatedAt = time.GetUtcNow(), ExpiresAt = time.GetUtcNow().Add(PreviewLifetime)
            };
            db.OrganizationFilings.Add(filing);
            await db.SaveChangesAsync();
            var op = new FileOperation { Kind = "Organize", RootId = root.Id, EntryId = entry.Id, FromPath = filing.SourcePath, ToPath = filing.DestinationPath,
                TargetParentId = filing.TargetFolderId, OrganizationFilingId = filing.Id, CreatedAt = time.GetUtcNow() };
            filing.State = "Applying"; filing.OperationId = op.Id;
            await ExecuteMoveAsync(db, root, entry, op);
            return op.Id;
        }
        finally { writer.Release(); }
    }

    // Runs inside the move's own transaction (also when Recovery completes an interrupted filing),
    // so the details, the new kind/sender/tags, the trust record and the example commit with it.
    private async Task CompleteSortingAsync(CatalogDb db, OrganizationFiling plan, FileOperation op)
    {
        var stored = JsonSerializer.Deserialize<StoredSort>(plan.DetailsJson)!;
        var choice = stored.Choice;
        var isNewKind = MatchKind(choice.Kind, await db.Kinds.ToListAsync()) == null;
        // An arrangement kept for the kind (or the arrangement a brand-new kind is made with) becomes the kind's patterns;
        // the document then follows them. One used for this document only leaves it where it was put.
        var keepArrangement = choice.KeepPatterns || isNewKind;
        var (kind, sender) = await SaveDetailsCoreAsync(db, op.EntryId, choice, plan.Actor, stored.KindDescription,
            followsFolder: choice.FolderOverride.Trim().Length == 0 && (choice.FolderPattern == null || keepArrangement),
            followsName: !choice.KeepName && choice.NameOverride.Trim().Length == 0 && (choice.NamePattern == null || keepArrangement));
        if (keepArrangement && (choice.FolderPattern != null || choice.NamePattern != null))
        {
            var changed = false;
            if (choice.FolderPattern != null && choice.FolderPattern != kind.FolderPattern) { kind.FolderPattern = SortPatterns.ValidatePattern(choice.FolderPattern, folder: true); changed = true; }
            if (choice.NamePattern != null && choice.NamePattern != kind.NamePattern) { kind.NamePattern = SortPatterns.ValidatePattern(choice.NamePattern, folder: false); changed = true; }
            if (changed && !isNewKind) kind.Revision++;
        }
        if (stored.HadSuggestion && !plan.Automatic)
        {
            var corrected = Key(stored.SuggestedKind) != Key(kind.Name) || Key(stored.SuggestedFrom) != Key(sender?.Name ?? "") || stored.SuggestedDate != choice.Date
                || choice.FolderOverride.Trim().Length > 0 || choice.NameOverride.Trim().Length > 0;
            kind.Approved++;
            if (corrected) { kind.Corrected++; kind.Streak = 0; } else kind.Streak++;
            // The reader's spelling of a sender, corrected to a known one, is remembered as that sender's.
            if (sender != null && stored.RawFrom.Length > 0 && Key(stored.RawFrom) != Key(sender.Name) && Aliases(sender.AliasesJson).All(x => Key(x) != Key(stored.RawFrom)))
                sender.AliasesJson = JsonSerializer.Serialize(CleanAliases(Aliases(sender.AliasesJson).Append(stored.RawFrom), sender.Name));
        }
        if (!plan.Automatic)
        {
            // What the person approved becomes the regular's memory; a suggestion that needed no correction adds to its streak.
            var filedText = await db.Entries.Where(x => x.Id == op.EntryId).Select(x => x.ExtractedText).SingleAsync();
            bool? routeCorrected = stored.HadSuggestion ? Key(stored.SuggestedKind) != Key(kind.Name) || Key(stored.SuggestedFrom) != Key(sender?.Name ?? "") || stored.SuggestedDate != choice.Date
                || choice.FolderOverride.Trim().Length > 0 || choice.NameOverride.Trim().Length > 0 : null;
            await RecordRouteAsync(db, kind, sender, choice, Path.GetFileName(plan.SourcePath), filedText, routeCorrected);
        }
        plan.State = "Filed"; plan.CompletedAt = time.GetUtcNow(); plan.OperationId = op.Id;
        if (plan.JobId is Guid jobId) { var job = await db.IdentificationJobs.SingleAsync(x => x.Id == jobId); job.State = "Filed"; }
        if (plan.Remember && !await db.FilingExamples.AnyAsync(x => x.Id == plan.Id))
            db.FilingExamples.Add(new() { Id = plan.Id, RootId = op.RootId, EntryId = op.EntryId, FolderId = plan.TargetFolderId, OriginalName = Path.GetFileName(plan.SourcePath),
                Filename = plan.Filename, DocumentType = kind.Name, Summary = choice.Title, Note = sender?.Name ?? "", Actor = plan.Actor, CreatedAt = time.GetUtcNow() });
    }

    private async Task<(DocumentKind Kind, Correspondent? Sender)> SaveDetailsCoreAsync(CatalogDb db, Guid entryId, SortChoice choice, string actor, string kindDescription,
        bool? followsFolder = null, bool? followsName = null)
    {
        var kinds = await db.Kinds.ToListAsync();
        var kind = MatchKind(choice.Kind, kinds);
        if (kind == null)
        {
            kind = new() { Name = KindName(choice.Kind), Description = kindDescription.Length > 400 ? kindDescription[..400] : kindDescription,
                FolderPattern = choice.FolderPattern != null ? SortPatterns.ValidatePattern(choice.FolderPattern, folder: true) : DefaultKindFolder,
                NamePattern = choice.NamePattern != null ? SortPatterns.ValidatePattern(choice.NamePattern, folder: false) : DefaultKindName, FieldsJson = JsonSerializer.Serialize(choice.Fields.Keys.Select(FactKey).Take(12)), CreatedAt = time.GetUtcNow() };
            db.Kinds.Add(kind);
        }
        Correspondent? sender = null;
        if (choice.From.Trim().Length > 0)
        {
            sender = MatchNamed(choice.From, await db.Correspondents.ToListAsync(), x => x.Name, x => Aliases(x.AliasesJson));
            if (sender == null) { sender = new() { Name = SenderName(choice.From), CreatedAt = time.GetUtcNow() }; db.Correspondents.Add(sender); }
        }
        var tags = await db.Tags.ToListAsync(); var tagIds = new List<Guid>();
        foreach (var raw in choice.Tags)
        {
            var tag = MatchNamed(raw, tags, x => x.Name, x => Aliases(x.AliasesJson));
            if (tag == null) { tag = new() { Name = TagName(raw), CreatedAt = time.GetUtcNow() }; db.Tags.Add(tag); tags.Add(tag); }
            if (!tagIds.Contains(tag.Id)) tagIds.Add(tag.Id);
        }
        var detail = await db.Details.SingleOrDefaultAsync(x => x.EntryId == entryId);
        if (detail == null) { detail = new() { EntryId = entryId, Revision = 0 }; db.Details.Add(detail); }
        detail.KindId = kind.Id; detail.CorrespondentId = sender?.Id; detail.DocumentDate = choice.Date; detail.Title = SortPatterns.Clean(choice.Title);
        detail.FieldsJson = JsonSerializer.Serialize(choice.Fields.Where(x => x.Value.Trim().Length > 0).ToDictionary(x => FactKey(x.Key), x => NormalizeFieldValue(FactKey(x.Key), x.Value)));
        if (followsFolder is bool folder) detail.FollowsFolder = folder;
        if (followsName is bool name) detail.FollowsName = name;
        detail.UpdatedAt = time.GetUtcNow(); detail.UpdatedBy = actor; detail.Revision++;
        await db.EntryTags.Where(x => x.EntryId == entryId).ExecuteDeleteAsync();
        foreach (var id in tagIds) db.EntryTags.Add(new() { EntryId = entryId, TagId = id });
        return (kind, sender);
    }

    // Details on a document already in the Library, without moving it.
    public async Task SaveDocumentDetailsAsync(Guid entryId, SortChoice choice, string actor)
    {
        Actor(actor);
        if (choice.Kind.Trim().Length == 0) throw new StorageException("Choose what kind of document this is.");
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == entryId && !x.IsFolder) ?? throw new StorageException("That document is no longer in the catalog.");
            await using var transaction = await db.Database.BeginTransactionAsync();
            await SaveDetailsCoreAsync(db, entry.Id, choice, actor, "");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        finally { writer.Release(); }
    }

    public async Task<DocumentDetailView?> DocumentDetailsAsync(Guid entryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var detail = await db.Details.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entryId);
        return detail == null ? null : await DetailViewAsync(db, detail);
    }

    private static async Task<DocumentDetailView> DetailViewAsync(CatalogDb db, DocumentDetail detail)
    {
        var kind = detail.KindId == null ? "" : await db.Kinds.Where(x => x.Id == detail.KindId).Select(x => x.Name).SingleOrDefaultAsync() ?? "";
        var from = detail.CorrespondentId == null ? "" : await db.Correspondents.Where(x => x.Id == detail.CorrespondentId).Select(x => x.Name).SingleOrDefaultAsync() ?? "";
        var tags = await db.EntryTags.Where(x => x.EntryId == detail.EntryId).Join(db.Tags, x => x.TagId, x => x.Id, (_, t) => t.Name).OrderBy(x => x).ToArrayAsync();
        Dictionary<string, string> fields;
        try { fields = JsonSerializer.Deserialize<Dictionary<string, string>>(detail.FieldsJson) ?? new(); } catch (JsonException) { fields = new(); }
        return new(detail.EntryId, kind, from, detail.DocumentDate, detail.Title, tags, fields, detail.Revision, detail.FollowsFolder, detail.FollowsName);
    }

    // ---------- The Filed list and Undo ----------

    public async Task<List<FiledItem>> RecentFilingsAsync(int take = 50)
    {
        await using var db = await factory.CreateDbContextAsync();
        var plans = await db.OrganizationFilings.AsNoTracking().Where(x => x.DetailsJson != "" && x.State == "Filed" && x.UndoneAt == null).OrderByDescending(x => x.CompletedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync();
        return plans.Select(plan =>
        {
            var stored = JsonSerializer.Deserialize<StoredSort>(plan.DetailsJson)!;
            var corrected = stored.HadSuggestion && (Key(stored.SuggestedKind) != Key(stored.Choice.Kind) || Key(stored.SuggestedFrom) != Key(stored.Choice.From) || stored.SuggestedDate != stored.Choice.Date);
            return new FiledItem(plan.Id, plan.EntryId, plan.Filename, plan.DestinationPath, plan.SourcePath, plan.DocumentType, stored.Choice.From, stored.Choice.Date, stored.Choice.Title,
                stored.Choice.Tags, plan.Automatic, plan.UndoneAt != null, corrected, plan.CompletedAt ?? plan.CreatedAt, plan.Actor, plan.ConfirmedAt != null);
        }).ToList();
    }

    // Puts a filed document back where it came from. Its details stay, so the card comes back
    // filled in. Undoing one that filed itself switches its kind back to asking first.
    public async Task UndoFilingAsync(Guid planId, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var plan = await db.OrganizationFilings.SingleOrDefaultAsync(x => x.Id == planId) ?? throw new StorageException("That filing is no longer on record.");
            if (plan.State != "Filed" || plan.DetailsJson.Length == 0) throw new StorageException("Only a completed filing can be undone.");
            if (plan.UndoneAt != null) throw new StorageException("That filing was already undone.");
            var (entry, root) = await WritableEntryAsync(db, plan.EntryId);
            if (entry.RelativePath != plan.DestinationPath) throw new StorageException("The document has moved since it was filed. Move it by hand from the Library.");
            var parentPath = plan.SourcePath.Contains('/') ? plan.SourcePath[..plan.SourcePath.LastIndexOf('/')] : "";
            var parent = await db.Entries.SingleOrDefaultAsync(x => x.RootId == root.Id && x.RelativePath == parentPath && x.IsFolder && !x.IsMissing && x.TrashBatchId == null)
                ?? throw new StorageException("The inbox it came from is gone. Move it by hand from the Library.");
            var op = new FileOperation { RootId = root.Id, EntryId = entry.Id, Kind = "Move", FromPath = entry.RelativePath, ToPath = plan.SourcePath, TargetParentId = parent.Id };
            await ExecuteMoveAsync(db, root, entry, op);
            plan.UndoneAt = time.GetUtcNow();
            var kind = await db.Kinds.SingleOrDefaultAsync(x => x.Name == plan.DocumentType);
            if (kind != null)
            {
                kind.Streak = 0;
                if (plan.Automatic) { kind.AutoFile = false; kind.Corrected++; kind.Revision++; }
            }
            // The regular it was filed as: start its run again, and stop it filing itself if it just did.
            var undone = await db.Details.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == plan.EntryId);
            if (kind != null && undone?.CorrespondentId is Guid undoneSender && await db.Routes.SingleOrDefaultAsync(x => x.KindId == kind.Id && x.CorrespondentId == undoneSender) is { } undoneRoute)
            {
                undoneRoute.Streak = 0; undoneRoute.NudgeAtStreak = RouteTrustStreak; undoneRoute.Revision++;
                if (plan.Automatic) { undoneRoute.AutoFile = false; undoneRoute.Corrected++; }
            }
            if (plan.JobId is Guid jobId) { var job = await db.IdentificationJobs.SingleAsync(x => x.Id == jobId); job.State = "Succeeded"; }
            await db.FilingExamples.Where(x => x.Id == plan.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.RemovedAt, time.GetUtcNow()).SetProperty(x => x.RemovedBy, actor));
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    // After a reading: file it now if its kind files itself and nothing in it was a guess.
    public async Task<bool> TryAutoFileAsync(Guid jobId)
    {
        SortCard? card;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var settings = await db.OllamaConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            if (settings?.AutoFileEnabled != true) return false;
            var job = await db.IdentificationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId);
            if (job == null || !job.Sorting || job.State != "Succeeded") return false;
            if (await db.Details.AnyAsync(x => x.EntryId == job.EntryId)) return false; // A person has had a hand in it.
            card = await SortCardAsync(job.EntryId);
            if (card == null || !card.Sure || card.JobId != jobId) return false;
            if (!await TrustedAsync(db, card)) return false;
        }
        try { await FileSortedCoreAsync(card.Document.Id, card.Choice with { Remember = false }, card.Document.ContentHash, "automatic", automatic: true); return true; }
        catch (StorageException) { return false; }
    }
}
