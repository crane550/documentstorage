using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

// Regulars: a kind from one sender, remembered from what the person approved. A recurring document
// whose sender is named in it can be filled in from memory with no model call (kind, title, tags,
// fields that never change, and the date picked the same way as before), and a regular can be
// trusted to file itself without trusting its whole kind.
public sealed record RouteSample(string Title, string[] Tags, Dictionary<string, string> Fields, string[] DateRules, DateTimeOffset At);
public sealed record RegularView(Guid Id, Guid KindId, string Kind, Guid SenderId, string Sender, int Approved, int Corrected, int Streak, bool AutoFile, bool KindAutoFile,
    int Remembered, bool Suggest);
internal sealed record MemorySuggestion(SortChoice Choice, Dictionary<string, string> Sources, List<string> Notes, bool Sure, Guid RouteId);

public sealed partial class LibraryService
{
    public const int RouteMemory = 5;
    public const int RouteMinimum = 3;
    public const int RouteTrustStreak = 5;
    private static readonly string[] DateRuleOrder = ["filename", "latest", "first", "earliest", "none"];

    // Which ways of picking a date from the document lead to the date the person chose.
    public static string[] DateRules(DateOnly? chosen, string fileName, string text)
    {
        if (chosen is not DateOnly date) return ["none"];
        var head = text.Length > DocumentHints.TextWindow ? text[..DocumentHints.TextWindow] : text;
        var found = DocumentHints.FindDates(head, "text").Select(x => x.Date).ToList();
        var rules = new List<string>();
        if (DocumentHints.FindDates(Path.GetFileNameWithoutExtension(fileName), "filename").Any(x => x.Date == date)) rules.Add("filename");
        if (found.Count > 0)
        {
            if (found.Max() == date) rules.Add("latest");
            if (found[0] == date) rules.Add("first");
            if (found.Min() == date) rules.Add("earliest");
        }
        return rules.Count == 0 ? ["unseen"] : rules.ToArray();
    }

    public static DateOnly? ApplyDateRule(string rule, string fileName, string text)
    {
        var head = text.Length > DocumentHints.TextWindow ? text[..DocumentHints.TextWindow] : text;
        var found = DocumentHints.FindDates(head, "text").Select(x => x.Date).ToList();
        return rule switch
        {
            "filename" => DocumentHints.FindDates(Path.GetFileNameWithoutExtension(fileName), "filename").Select(x => (DateOnly?)x.Date).FirstOrDefault(),
            "latest" => found.Count == 0 ? null : found.Max(),
            "first" => found.Count == 0 ? null : found[0],
            "earliest" => found.Count == 0 ? null : found.Min(),
            _ => null
        };
    }

    private static List<RouteSample> Samples(SortRoute route)
    {
        try { return JsonSerializer.Deserialize<List<RouteSample>>(route.RecentJson) ?? []; } catch (JsonException) { return []; }
    }

    // Called inside the same transaction as the filing or the accepted suggestion it records.
    private async Task RecordRouteAsync(CatalogDb db, DocumentKind kind, Correspondent? sender, SortChoice choice, string fileName, string text, bool? corrected)
    {
        if (sender == null) return;
        var route = db.Routes.Local.FirstOrDefault(x => x.KindId == kind.Id && x.CorrespondentId == sender.Id)
            ?? await db.Routes.SingleOrDefaultAsync(x => x.KindId == kind.Id && x.CorrespondentId == sender.Id);
        if (route == null) { route = new() { KindId = kind.Id, CorrespondentId = sender.Id, NudgeAtStreak = RouteTrustStreak }; db.Routes.Add(route); }
        route.Approved++;
        // Streaks measure suggestions that needed no correction; a filing done by hand from scratch says nothing about them.
        if (corrected == true) { route.Corrected++; route.Streak = 0; }
        else if (corrected == false) route.Streak++;
        var samples = Samples(route);
        samples.Insert(0, new(SortPatterns.Clean(choice.Title), choice.Tags.Select(x => { try { return TagName(x); } catch (StorageException) { return ""; } }).Where(x => x.Length > 0).ToArray(),
            choice.Fields.Where(x => x.Value.Trim().Length > 0).ToDictionary(x => x.Key, x => x.Value.Trim()), DateRules(choice.Date, fileName, text), time.GetUtcNow()));
        route.RecentJson = JsonSerializer.Serialize(samples.Take(RouteMemory));
        route.UpdatedAt = time.GetUtcNow(); route.Revision++;
    }

    // Fills in a card from what was approved for the same sender before. Only when the sender is
    // named in the document, one kind clearly dominates for it, and there are enough examples.
    private static MemorySuggestion? FromMemory(CatalogEntry entry, Vocab vocab, DocumentHintSet hints, List<SortRoute> routes)
    {
        if (hints.Senders.Count == 0) return null;
        var senderId = hints.Senders[0];
        var mine = routes.Where(x => x.CorrespondentId == senderId && x.Approved > 0).OrderByDescending(x => x.Approved).ThenByDescending(x => x.UpdatedAt).ToList();
        if (mine.Count == 0) return null;
        var route = mine[0];
        var samples = Samples(route).Take(RouteMinimum).ToList();
        if (samples.Count < RouteMinimum) return null;
        // Another kind from the same sender almost as often: the sender alone doesn't say which.
        if (mine.Count > 1 && mine[1].Approved * 3 > route.Approved) return null;
        var kind = vocab.Kinds.FirstOrDefault(x => x.Id == route.KindId);
        var sender = vocab.Senders.FirstOrDefault(x => x.Id == senderId);
        if (kind == null || sender == null) return null;
        var sources = new Dictionary<string, string> { ["kind"] = "memory", ["from"] = "text" };
        var notes = new List<string>();
        var titles = samples.Select(x => x.Title).Distinct(StringComparer.Ordinal).ToList();
        var title = samples[0].Title; var titleSteady = titles.Count == 1;
        if (title.Length > 0) sources["title"] = titleSteady ? "memory" : "guess";
        var tags = samples.Select(x => (IEnumerable<string>)x.Tags).Aggregate((a, b) => a.Intersect(b)).Where(x => vocab.TagNamed(x) != null).ToArray();
        if (tags.Length > 0) sources["tags"] = "memory";
        var fields = new Dictionary<string, string>();
        foreach (var field in KindFields(kind))
        {
            var values = samples.Select(x => x.Fields.GetValueOrDefault(field, "")).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count == 1 && values[0].Length > 0 && QuoteFound(entry.ExtractedText, values[0])) fields[field] = values[0];
        }
        var rule = DateRuleOrder.FirstOrDefault(r => samples.All(x => x.DateRules.Contains(r)));
        DateOnly? date = rule == null || rule == "none" ? null : ApplyDateRule(rule, entry.Name, entry.ExtractedText);
        var dateOk = rule == "none" || date != null;
        if (date != null) sources["date"] = "memory";
        else if (rule == null) notes.Add("The date was picked differently each time; check it");
        var tokens = SortPatterns.TokensIn(kind.FolderPattern + "/" + kind.NamePattern);
        var missing = KindFields(kind).Where(tokens.Contains).Where(x => !fields.ContainsKey(x)).ToList();
        if (missing.Count > 0) notes.Add("Needs " + string.Join(", ", missing.Select(x => x.Replace('_', ' '))));
        var sure = (dateOk || !tokens.Contains("date")) && missing.Count == 0 && (titleSteady || !tokens.Contains("title"));
        notes.Insert(0, $"Filled in from the last {samples.Count} {kind.Name.ToLowerInvariant()}s from {sender.Name}");
        return new(new(kind.Name, sender.Name, date, title, tags, fields, KeepName: kind.KeepOriginalName ?? hints.DescriptiveName), sources, notes, sure, route.Id);
    }

    private static async Task<List<SortRoute>> RoutesForAsync(CatalogDb db, IEnumerable<Guid> senderIds)
    {
        var ids = senderIds.ToArray();
        return ids.Length == 0 ? [] : await db.Routes.AsNoTracking().Where(x => ids.Contains(x.CorrespondentId)).ToListAsync();
    }

    // A card may file itself when its kind does, or its regular (this kind from this sender) does.
    private static async Task<bool> TrustedAsync(CatalogDb db, SortCard card)
    {
        var kind = await db.Kinds.AsNoTracking().SingleOrDefaultAsync(x => x.Name == card.Choice.Kind);
        if (kind == null) return false;
        if (kind.AutoFile) return true;
        var sender = await db.Correspondents.AsNoTracking().SingleOrDefaultAsync(x => x.Name == card.Choice.From);
        return sender != null && await db.Routes.AnyAsync(x => x.KindId == kind.Id && x.CorrespondentId == sender.Id && x.AutoFile);
    }

    // Inbox documents a trusted regular recognises from memory file themselves, with no model call.
    // Runs whether or not the reader is on.
    public async Task<int> AutoFileFromMemoryAsync(int max = 25)
    {
        List<Guid> ids;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var settings = await db.OllamaConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            if (settings?.AutoFileEnabled == false) return 0;
            if (!await db.Routes.AnyAsync(x => x.AutoFile) && !await db.Kinds.AnyAsync(x => x.AutoFile)) return 0;
            ids = await (await IntakeQueryAsync(db)).Where(x => x.Readiness == "Ready" && !db.Details.Any(d => d.EntryId == x.Id)
                    && !db.IdentificationJobs.Any(j => j.EntryId == x.Id && (j.State == "Queued" || j.State == "Running" || j.State == "Retry"))
                    && !db.OrganizationFilings.Any(f => f.EntryId == x.Id && f.UndoneAt != null))
                .OrderBy(x => x.ObservedAt).ThenBy(x => x.Id).Select(x => x.Id).Take(Math.Clamp(max, 1, 100)).ToListAsync();
        }
        var filed = 0;
        foreach (var id in ids)
        {
            var card = await SortCardAsync(id);
            if (card == null || !card.Sure || card.Sources.GetValueOrDefault("kind") != "memory") continue;
            await using (var db = await factory.CreateDbContextAsync()) if (!await TrustedAsync(db, card)) continue;
            try { await FileSortedCoreAsync(id, card.Choice with { Remember = false }, card.Document.ContentHash, "automatic", automatic: true); filed++; }
            catch (StorageException) { }
        }
        return filed;
    }

    public async Task<List<RegularView>> RegularsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var routes = await db.Routes.AsNoTracking().OrderByDescending(x => x.Approved).ThenBy(x => x.Id).ToListAsync();
        var kinds = await db.Kinds.AsNoTracking().ToDictionaryAsync(x => x.Id);
        var senders = await db.Correspondents.AsNoTracking().ToDictionaryAsync(x => x.Id);
        return routes.Where(x => kinds.ContainsKey(x.KindId) && senders.ContainsKey(x.CorrespondentId)).Select(x =>
        {
            var kind = kinds[x.KindId];
            return new RegularView(x.Id, x.KindId, kind.Name, x.CorrespondentId, senders[x.CorrespondentId].Name, x.Approved, x.Corrected, x.Streak, x.AutoFile, kind.AutoFile,
                Samples(x).Count, !x.AutoFile && !kind.AutoFile && x.Streak >= Math.Max(RouteTrustStreak, x.NudgeAtStreak));
        }).ToList();
    }

    public async Task SetRegularAutoFileAsync(Guid routeId, bool on, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var route = await db.Routes.SingleOrDefaultAsync(x => x.Id == routeId) ?? throw new StorageException("That regular is no longer on record.");
            route.AutoFile = on; route.Revision++;
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    // "Not now": ask again after five more right in a row.
    public async Task SnoozeRegularAsync(Guid routeId, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var route = await db.Routes.SingleOrDefaultAsync(x => x.Id == routeId) ?? throw new StorageException("That regular is no longer on record.");
            route.NudgeAtStreak = route.Streak + RouteTrustStreak; route.Revision++;
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    // "Looks right" on something that filed itself: it counts toward trust like an approval.
    public async Task ConfirmFilingAsync(Guid planId, string actor)
    {
        Actor(actor);
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var plan = await db.OrganizationFilings.SingleOrDefaultAsync(x => x.Id == planId) ?? throw new StorageException("That filing is no longer on record.");
            if (!plan.Automatic || plan.State != "Filed" || plan.UndoneAt != null) throw new StorageException("Only something that filed itself can be confirmed.");
            if (plan.ConfirmedAt != null) return;
            plan.ConfirmedAt = time.GetUtcNow();
            // Now a person stands behind it, so later readings may learn from it.
            var detail = await db.Details.SingleOrDefaultAsync(x => x.EntryId == plan.EntryId);
            if (detail != null) detail.Confirmed = true;
            var kind = detail?.KindId == null ? null : await db.Kinds.SingleOrDefaultAsync(x => x.Id == detail.KindId);
            if (kind != null) { kind.Approved++; kind.Streak++; }
            if (detail?.CorrespondentId is Guid senderId && kind != null)
            {
                var route = await db.Routes.SingleOrDefaultAsync(x => x.KindId == kind.Id && x.CorrespondentId == senderId);
                if (route != null) { route.Approved++; route.Streak++; route.Revision++; }
            }
            await db.SaveChangesAsync();
        }
        finally { writer.Release(); }
    }

    // Once, for catalogs filed before regulars existed: replay the approved filings, oldest first.
    // Streaks start at zero (there is no record of which suggestions were right).
    public async Task<int> RebuildRegularsAsync()
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            if (await db.Routes.AnyAsync()) return 0;
            var filings = await db.OrganizationFilings.AsNoTracking().Where(x => x.DetailsJson != "" && x.State == "Filed" && x.UndoneAt == null && !x.Automatic)
                .OrderBy(x => x.CompletedAt).ThenBy(x => x.Id).Take(5000).ToListAsync();
            if (filings.Count == 0) return 0;
            var kinds = await db.Kinds.ToListAsync(); var senders = await db.Correspondents.ToListAsync();
            var count = 0;
            foreach (var filing in filings)
            {
                StoredSort? stored;
                try { stored = JsonSerializer.Deserialize<StoredSort>(filing.DetailsJson); } catch (JsonException) { continue; }
                if (stored == null) continue;
                var kind = MatchKind(stored.Choice.Kind, kinds);
                var sender = MatchNamed(stored.Choice.From, senders, x => x.Name, x => Aliases(x.AliasesJson));
                if (kind == null || sender == null) continue;
                var text = await db.Entries.Where(x => x.Id == filing.EntryId).Select(x => x.ExtractedText).SingleOrDefaultAsync() ?? "";
                await RecordRouteAsync(db, kind, sender, stored.Choice, Path.GetFileName(filing.SourcePath), text, null);
                count++;
            }
            await db.SaveChangesAsync();
            return count;
        }
        finally { writer.Release(); }
    }

    // Merging senders carries their regulars across, adding up records where both had one.
    private static async Task MergeRoutesAsync(CatalogDb db, Guid fromId, Guid intoId)
    {
        var moving = await db.Routes.Where(x => x.CorrespondentId == fromId).ToListAsync();
        foreach (var route in moving)
        {
            var target = await db.Routes.SingleOrDefaultAsync(x => x.CorrespondentId == intoId && x.KindId == route.KindId);
            if (target == null) { route.CorrespondentId = intoId; continue; }
            target.Approved += route.Approved; target.Corrected += route.Corrected;
            target.Streak = Math.Min(target.Streak, route.Streak); target.AutoFile = target.AutoFile && route.AutoFile;
            target.RecentJson = JsonSerializer.Serialize(Samples(target).Concat(Samples(route)).OrderByDescending(x => x.At).Take(RouteMemory));
            target.Revision++;
            db.Routes.Remove(route);
        }
    }
}
