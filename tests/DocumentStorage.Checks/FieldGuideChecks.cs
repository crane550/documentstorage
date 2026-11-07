using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

// How each field is found: types, looser matching, printed words, Find with AI and Set up fields with AI.
internal static class FieldGuideChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool, string> check, Func<Func<Task>, string, Task> refuses)
    {
        check(LibraryService.DefaultFieldType("beginning_balance") == "money" && LibraryService.DefaultFieldType("statement_end_date") == "date" && LibraryService.DefaultFieldType("due_date") == "date"
            && LibraryService.DefaultFieldType("account_number") == "id" && LibraryService.DefaultFieldType("account_ending") == "id" && LibraryService.DefaultFieldType("account_description") == "text"
            && LibraryService.DefaultFieldType("tax_year") == "number", "a field's type is guessed from its name until someone says");
        check(LibraryService.MoneyText("$1,234.56") == "1234.56" && LibraryService.MoneyText("(45.10)") == "-45.10" && LibraryService.MoneyText("-$3") == "-3.00"
            && LibraryService.MoneyText("1,204") == "1204.00" && LibraryService.MoneyText("about 20") == null, "money is kept as 1234.56, negatives with a minus");
        var money = new FieldGuide("money", "", []); var id = new FieldGuide("id", "", []); var date = new FieldGuide("date", "", []); var text = new FieldGuide("text", "", []);
        const string page = "SYNTHETIC CEDAR BANK\nAccount Number: ****5678\nStatement period May 1, 2025 to May 31, 2025\nBeginning balance    $1,204.33\nEnding Balance  $980.10\nAccount type: Everyday checking";
        check(LibraryService.ValueFound(page, "1204.33", money) && LibraryService.ValueFound(page, "$980.10", money) && !LibraryService.ValueFound(page, "1204.34", money),
            "a money value is found however the document writes it, and only that amount");
        check(LibraryService.ValueFound(page, "XXXX5678", id) && LibraryService.ValueFound(page, "5678", id) && !LibraryService.ValueFound(page, "9999", id),
            "an account number is found through its mask");
        check(LibraryService.ValueFound(page, "2025-05-31", date) && !LibraryService.ValueFound(page, "2025-06-30", date), "a date is found in any written form");
        check(LibraryService.ValueFound(page, "everyday CHECKING", text) && !LibraryService.ValueFound(page, "Savings", text), "text is found whatever its capitals and spacing");
        check(LibraryService.NormalizeFieldValue("x", "$1,204.33", money) == "1204.33" && LibraryService.NormalizeFieldValue("x", "May 31, 2025", date) == "2025-05-31"
            && LibraryService.NormalizeFieldValue("x", "****5678", id) == "5678" && LibraryService.NormalizeFieldValue("x", "XXXX-12AB4", id) == "12AB4"
            && LibraryService.NormalizeFieldValue("x", "77-1200", id) == "77-1200", "values are kept in one form per type, a masked number by the digits it shows");
        check(LibraryService.LabelFind(page, new("money", "", ["Beginning balance", "Opening balance"])) is { Value: "1204.33" } found && found.Quote.Contains("Beginning balance"),
            "code finds a value by the words printed beside it");
        check(LibraryService.LabelFind(page, new("id", "", ["Account number"]))?.Value == "****5678" && LibraryService.LabelFind(page, new("text", "", ["Account type"]))?.Value == "Everyday checking"
            && LibraryService.LabelFind(page, new("money", "", ["Closing balance"])) == null, "each type takes its own shape after the words, and missing words find nothing");
        check(LibraryService.LabelFind("Total due\n$55.00\n", new("money", "", ["Total due"]))?.Value == "55.00", "a value on the line after its words is found too");
        var longText = new string('x', 20000) + "\nBeginning balance $7.00\n";
        var excerpt = LibraryService.FieldExcerpt(longText, ["Beginning balance"], 6000);
        check(excerpt.Length <= 6100 && excerpt.Contains("Beginning balance $7.00"), "a long document's excerpt keeps the passages where the printed words are");
        var clean = LibraryService.CleanGuide("total", new("weird", new string('a', 400), ["Total", "Total", "x", "Amount due"]));
        check(clean.Type == "money" && clean.Look.Length == 240 && clean.Labels.SequenceEqual(["Total", "Amount due"]), "a guide is cleaned: a known type, a short line, distinct printed words");

        Directory.CreateDirectory(scratch);
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock); const string actor = "guides-owner";
        var root = await library.AddRootAsync("Synthetic guides archive", scratch, false);
        var top = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var inbox = await library.CreateFolderAsync(top.Id, "Inbox"); await library.SetIntakeAsync(inbox, true);
        await library.SaveKindAsync(new() { Name = "Checking statement", Description = "Checking account statements.", FolderPattern = "Checking/{from}", NamePattern = "{date} {from} statement",
            FieldsJson = JsonSerializer.Serialize(new[] { "account_number", "beginning_balance", "ending_balance", "account_type" }) }, actor);
        var kind = await library.SetFieldGuideAsync("Checking statement", "beginning_balance", new("money", "The balance at the start of the statement period.", ["Beginning balance", "Opening balance"]), actor);
        check(LibraryService.FieldGuides(kind)["beginning_balance"] is { Type: "money", Labels.Length: 2 } && LibraryService.FieldGuides(kind)["ending_balance"].Type == "money",
            "describing a field keeps its guide on the kind; the others keep their guessed types");
        await refuses(() => library.SetFieldGuideAsync("Checking statement", "not_on_it", new("text", "", []), actor), "only a field the kind carries can be described");
        var resaved = await library.SaveKindAsync(new() { Id = kind.Id, Revision = kind.Revision, Name = kind.Name, Description = kind.Description, FolderPattern = kind.FolderPattern, NamePattern = kind.NamePattern,
            FieldsJson = JsonSerializer.Serialize(new[] { "account_number", "beginning_balance" }), FieldGuideJson = kind.FieldGuideJson }, actor);
        check(LibraryService.ReadGuides(resaved.FieldGuideJson).Keys.SequenceEqual(["beginning_balance"]), "saving a kind keeps the guides of the fields it still carries, and drops the rest");
        kind = await library.SaveKindAsync(new() { Id = resaved.Id, Revision = resaved.Revision, Name = resaved.Name, Description = resaved.Description, FolderPattern = resaved.FolderPattern, NamePattern = resaved.NamePattern,
            FieldsJson = JsonSerializer.Serialize(new[] { "account_number", "beginning_balance", "ending_balance", "account_type" }), FieldGuideJson = resaved.FieldGuideJson }, actor);

        var settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = true; settings.Model = "synthetic-sorter";
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        await library.SaveSenderAsync(Guid.Empty, "Synthetic Cedar Bank", [], actor);
        await library.UploadAsync(inbox, "scan-g1.txt", new MemoryStream(Encoding.UTF8.GetBytes(page))); clock.Advance(6); await library.ScanAsync(); clock.Advance(6); await library.ScanAsync();
        var entry = (await library.BrowseAsync(inbox)).Items.Single(x => x.Name == "scan-g1.txt");

        // The reading: guides go into the prompt; a value written differently still counts; one that isn't there is left empty and said so.
        await library.QueueSortingAsync(entry.Id, actor);
        SortRequest? sent = null;
        await library.ProcessNextIdentificationAsync(new Model(sort: input => { sent = input; return new("Checking statement", "", "Synthetic Cedar Bank", "2025-05-31", "Checking statement", [],
            [new("ending_balance", "$980.10", "Ending balance 980.10"), new("account_type", "Gold savings", "Gold savings")], "high"); }));
        using (var prompt = JsonDocument.Parse(sent!.PromptJson))
        {
            var notes = prompt.RootElement.GetProperty("known_kinds").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Checking statement").GetProperty("field_notes");
            check(notes.GetProperty("beginning_balance").GetString()!.Contains("start of the statement period") && notes.GetProperty("beginning_balance").GetString()!.Contains("Opening balance"),
                "the reader is told what each field is and the words printed beside it");
        }
        var card = (await library.SortCardAsync(entry.Id))!;
        check(card.Choice.Fields.GetValueOrDefault("ending_balance") == "980.10" && card.Sources.GetValueOrDefault("field:ending_balance") == "reader",
            "a reader's value written differently from the text still counts, kept in money form");
        check(!card.Choice.Fields.ContainsKey("account_type") && card.Notes.Any(x => x.Contains("Account type") && x.Contains("Gold savings")),
            "a value that isn't in the text is left empty, and the slip says why");
        check(card.Choice.Fields.GetValueOrDefault("beginning_balance") == "1204.33" && card.Sources.GetValueOrDefault("field:beginning_balance") == "text",
            "an empty field with printed words is filled from the text without the reader");

        // Find with AI: printed words first, the reader for the rest, every value checked.
        FieldFindRequest? asked = null;
        var finder = new Model(find: input => { asked = input; return new([new("account_number", "XXXX5678", "Account Number: ****5678"), new("account_type", "Business checking", "Business checking")]); });
        var finding = await library.FindFieldsAsync(entry.Id, "Checking statement", ["account_number", "beginning_balance", "account_type"], "Synthetic Cedar Bank", new DateOnly(2025, 5, 31), actor, finder);
        check(asked != null && asked.Names.SequenceEqual(["account_number", "account_type"]), "the reader is asked only for the fields code couldn't find");
        using (var prompt = JsonDocument.Parse(asked!.PromptJson))
            check(prompt.RootElement.GetProperty("fields").EnumerateArray().Any(x => x.GetProperty("name").GetString() == "account_number" && x.GetProperty("type").GetString() == "id")
                && prompt.RootElement.GetProperty("text").GetString()!.Contains("Account Number"), "each field goes with its type and description, and the text goes with them");
        check(finding.Values["beginning_balance"] == "1204.33" && finding.Sources["beginning_balance"] == "text" && finding.Values["account_number"] == "5678" && finding.Sources["account_number"] == "reader"
            && !finding.Values.ContainsKey("account_type") && finding.Notes.Any(x => x.Contains("Business checking")) && finding.AskedReader,
            "Find with AI offers what it found, says where from, and leaves out what isn't in the text");
        var none = await library.FindFieldsAsync(entry.Id, "Checking statement", ["beginning_balance"], "", null, actor, new Model());
        check(!none.AskedReader && none.Values["beginning_balance"] == "1204.33", "when code finds everything, the reader isn't asked at all");
        await refuses(() => library.FindFieldsAsync(entry.Id, "Checking statement", ["account_type"], "", null, actor, new Model(find: _ =>
        {
            File.AppendAllText(Path.Combine(scratch, "Inbox", "scan-g1.txt"), "\nchanged");
            clock.Advance(6); library.ScanAsync().GetAwaiter().GetResult(); clock.Advance(6); library.ScanAsync().GetAwaiter().GetResult();
            return new([]);
        })), "an answer about text that changed while the reader looked is refused");
        entry = (await library.BrowseAsync(inbox)).Items.Single(x => x.Name == "scan-g1.txt");

        // Set up fields with AI: a draft to review; nothing changes until it's kept.
        var draft = await library.DraftKindAsync(entry.Id, "Checking statement", actor, new Model(draft: _ => new("Statements for checking accounts at a bank.",
        [
            new("Beginning balance", "money", "", ["Beginning balance"], "1,204.33", "Beginning balance    $1,204.33"),
            new("Statement end date", "date", "The last day the statement covers.", ["to"], "May 31, 2025", "to May 31, 2025"),
            new("Routing number", "id", "The bank's routing number.", ["Routing"], "021000021", "Routing 021000021"),
            new("Title", "text", "", [], "", ""), new("from", "text", "", [], "", "")
        ])));
        var keys = draft.Fields.Select(x => x.Key).ToList();
        check(keys.Take(4).SequenceEqual(["account_number", "beginning_balance", "ending_balance", "account_type"]) && keys.Contains("statement_end_date") && !keys.Contains("title") && !keys.Contains("from"),
            "the draft keeps the kind's own fields first, adds the suggested ones, and never the details already recorded");
        var end = draft.Fields.Single(x => x.Key == "statement_end_date"); var routing = draft.Fields.Single(x => x.Key == "routing_number");
        check(end.Example == "2025-05-31" && !end.Existing && routing.Example == "" && draft.Fields.Single(x => x.Key == "beginning_balance").Guide.Look == "The balance at the start of the statement period.",
            "examples are checked against the text, and a described field keeps its description over an empty suggestion");
        check(LibraryService.KindFields((await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Checking statement")).Length == 4, "a draft changes nothing on its own");
        kind = await library.SaveKindDraftAsync("Checking statement", draft.Description, draft.Fields.Where(x => x.Key != "routing_number").Select(x => (x.Key, x.Guide)).ToList(), "", "", actor);
        check(LibraryService.KindFields(kind).Contains("statement_end_date") && !LibraryService.KindFields(kind).Contains("routing_number") && LibraryService.FieldGuides(kind)["statement_end_date"].Type == "date"
            && kind.Description == "Statements for checking accounts at a bank.", "keeping the draft adds the ticked fields with their guides and the description");
        var made = await library.SaveKindDraftAsync("Synthetic lease", "Leases for rented places.", [("rent", new FieldGuide("money", "Monthly rent.", ["Rent"]))], "Leases/{from}", "{date} {from} lease", actor);
        check(made.Name == "Synthetic lease" && made.FolderPattern == "Leases/{from}" && LibraryService.FieldGuides(made)["rent"].Labels.SequenceEqual(["Rent"]),
            "keeping a draft for a kind not made yet makes it, with the slip's arrangement");
        await refuses(() => library.SaveKindDraftAsync("Checking statement", null, Enumerable.Range(1, 12).Select(i => ("extra_" + i, new FieldGuide("text", "", []))).ToList(), "", "", actor),
            "a draft can't take a kind past its field limit");

        settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = false; settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        await refuses(() => library.DraftKindAsync(entry.Id, "Checking statement", actor, new Model()), "setting up fields needs the reader turned on");
        await library.SetIntakeAsync(inbox, false);
    }

    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds); }
    private sealed class Model(Func<SortRequest, SortReply>? sort = null, Func<FieldFindRequest, FieldReply>? find = null, Func<KindDraftRequest, KindDraftReply>? draft = null) : IOllamaClient
    {
        public Task<List<LocalModel>> ModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(new List<LocalModel>());
        public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Sorting never asks the old question.");
        public Task<SortReply> SortAsync(SortRequest request, CancellationToken cancellationToken = default) => Task.FromResult(sort!(request));
        public Task<FieldReply> FindFieldsAsync(FieldFindRequest request, CancellationToken cancellationToken = default) =>
            find != null ? Task.FromResult(find(request)) : throw new InvalidOperationException("The reader wasn't expected to be asked.");
        public Task<KindDraftReply> DraftKindAsync(KindDraftRequest request, CancellationToken cancellationToken = default) => Task.FromResult(draft!(request));
    }
}
