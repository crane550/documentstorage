using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

// Configure by using: fields typed on one slip, kept for the kind, and put into its name or folder.
internal static class KindFieldChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool, string> check, Func<Func<Task>, string, Task> refuses)
    {
        check(LibraryService.FieldKey("Statement Beginning Date") == "statement_beginning_date" && LibraryService.FieldKey("  Account #  ") == "account"
            && LibraryService.FieldKey("401k plan") == "f_401k_plan" && LibraryService.FieldLabel("statement_beginning_date") == "Statement beginning date",
            "a field typed in plain words gets a key, and the key reads back as words");
        check(LibraryService.IsDateField("statement_end_date") && !LibraryService.IsDateField("account_ending") && !LibraryService.IsDateField("update_notes"),
            "a field is a date when its name has the word date");
        check(LibraryService.NormalizeFieldValue("statement_end_date", "October 31, 2026") == "2026-10-31" && LibraryService.NormalizeFieldValue("statement_end_date", "soon") == "soon"
            && LibraryService.NormalizeFieldValue("account_number", "10/31/2026") == "10/31/2026", "date fields are kept as yyyy-MM-dd when they read as a date; others as typed");

        // Patterns as chips round-trip.
        foreach (var pattern in new[] { "{date:yyyy-MM} {from} statement {account_ending}", "{title}", "Banking - {from}" })
            check(PatternParts.Join(PatternParts.Parse(pattern)) == pattern, "a name pattern splits into chips and joins back unchanged: " + pattern);
        check(PatternParts.JoinFolder(PatternParts.ParseFolder("Banking/{from}/{date:yyyy}")) == "Banking/{from}/{date:yyyy}"
            && PatternParts.ParseFolder("Banking/{from}/{date:yyyy}").Count == 3, "a folder pattern splits into levels of chips and joins back");
        check(PatternParts.Join([PatternPart.Words("a/b{c}")]) == "a-b(c)", "typed words in a chip can't add folders or tokens");
        check(PatternParts.NextFormat(null) == "yyyy-MM" && PatternParts.NextFormat("MMMM") == null && PatternParts.Label(PatternPart.Of("statement_end_date", "yyyy")) == "Statement end date (year)",
            "a date chip cycles through how it's written, and says so");

        Directory.CreateDirectory(scratch);
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock); const string actor = "fields-owner";
        var root = await library.AddRootAsync("Synthetic fields archive", scratch, false);
        var top = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var inbox = await library.CreateFolderAsync(top.Id, "Inbox"); await library.SetIntakeAsync(inbox, true);
        await library.SaveKindAsync(new() { Name = "Brokerage statement", Description = "Investment account statements.", FolderPattern = "Investments/{from}", NamePattern = "{date} {from} statement" }, actor);
        await refuses(() => library.AddKindFieldAsync("Never approved kind", "account_number", actor), "a kind that doesn't exist yet can't be changed; its fields travel with the document");
        await refuses(() => library.AddKindFieldAsync("Brokerage statement", "filename", actor), "reserved names can't be fields");
        var kind = await library.AddKindFieldAsync("Brokerage statement", LibraryService.FieldKey("Statement End Date"), actor);
        await library.AddKindFieldAsync("Brokerage statement", "statement_end_date", actor);
        check(LibraryService.KindFields(kind).SequenceEqual(["statement_end_date"]), "keeping a field adds it to the kind once");
        kind = await library.SetFieldInPatternAsync("Brokerage statement", "statement_end_date", folder: false, on: true, actor);
        check(kind.NamePattern == "{date} {from} statement {statement_end_date}", "Name adds the field to the end of the name pattern");
        kind = await library.SetFieldInPatternAsync("Brokerage statement", "account_number", folder: true, on: true, actor);
        check(kind.FolderPattern == "Investments/{from}/{account_number}" && LibraryService.KindFields(kind).Contains("account_number"), "Folder adds a folder level, and keeps the field if it wasn't yet");
        kind = await library.SetFieldInPatternAsync("Brokerage statement", "statement_end_date", folder: true, on: true, actor);
        check(kind.FolderPattern.EndsWith("/{statement_end_date:yyyy}"), "a date field files by its year when used as a folder");
        kind = await library.SetFieldInPatternAsync("Brokerage statement", "statement_end_date", folder: true, on: false, actor);
        kind = await library.SetFieldInPatternAsync("Brokerage statement", "statement_end_date", folder: false, on: false, actor);
        check(kind.FolderPattern == "Investments/{from}/{account_number}" && kind.NamePattern == "{date} {from} statement" && LibraryService.KindFields(kind).Contains("statement_end_date"),
            "turning them off takes the field out of the patterns and leaves it on the kind");
        kind = await library.SetFieldInPatternAsync("Brokerage statement", "statement_end_date", folder: false, on: true, actor);

        // A document: the reader's written-out date becomes yyyy-MM-dd, and the field shapes the path.
        var settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = true; settings.Model = "synthetic-sorter";
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        await library.SaveSenderAsync(Guid.Empty, "Oak Brokerage", [], actor);
        const string text = "Oak Brokerage\nAccount number 77-1200\nStatement period ending October 31, 2026\nSynthetic total 1,000.00";
        await library.UploadAsync(inbox, "scan-a.txt", new MemoryStream(Encoding.UTF8.GetBytes(text))); clock.Advance(6); await library.ScanAsync(); clock.Advance(6); await library.ScanAsync();
        var entry = (await library.BrowseAsync(inbox)).Items.Single(x => x.Name == "scan-a.txt");
        await library.QueueSortingAsync(entry.Id, actor);
        AskRequest? sent = null;
        await library.ProcessNextIdentificationAsync(new ScriptedModel(input => { if (input.Step == "details") sent = input; return new("Brokerage statement", "", "Oak Brokerage", "2026-10-31", "Brokerage statement", [],
            [new("statement_end_date", "October 31, 2026", "ending October 31, 2026"), new("account_number", "77-1200", "Account number 77-1200")], "high"); }));
        check(sent!.Question.Contains("filed as \"Brokerage statement\"") && sent.Question.Contains("- statement_end_date"), "a kept field is asked of the reader from then on");
        var card = (await library.SortCardAsync(entry.Id))!;
        check(card.Choice.Fields["statement_end_date"] == "2026-10-31" && card.Plan.FolderPath == "Investments/Oak Brokerage/77-1200" && card.Plan.FileName == "2026-10-31 Oak Brokerage statement 2026-10-31.txt",
            "a written-out date from the reader is stored as yyyy-MM-dd, and kept fields fill in the folder and name");
        var oneOff = card.Choice with { Fields = new(card.Choice.Fields) { ["advisor"] = "Synthetic Advisor" } };
        await library.FileSortedAsync(entry.Id, oneOff, card.Document.ContentHash, actor);
        var saved = (await library.DocumentDetailsAsync(entry.Id))!;
        check(saved.Fields["advisor"] == "Synthetic Advisor" && !LibraryService.KindFields((await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Brokerage statement")).Contains("advisor"),
            "a field added on one document is saved on that document without changing the kind");

        // An arrangement for one document, then one kept for the kind, then a new kind made with one.
        async Task<SortCard> Next(string name, string body)
        {
            await library.UploadAsync(inbox, name, new MemoryStream(Encoding.UTF8.GetBytes(body))); clock.Advance(6); await library.ScanAsync(); clock.Advance(6); await library.ScanAsync();
            return (await library.SortCardAsync((await library.BrowseAsync(inbox)).Items.Single(x => x.Name == name).Id))!;
        }
        var b = await Next("scan-b.txt", text.Replace("October", "November"));
        var arranged = b.Choice with { Kind = "Brokerage statement", From = "Oak Brokerage", Date = new DateOnly(2026, 11, 30), Fields = new() { ["account_number"] = "77-1200", ["statement_end_date"] = "2026-11-30" },
            FolderPattern = "Investments/{account_number}/{date:yyyy}", NamePattern = "{statement_end_date:yyyy-MM} {from}", KeepPatterns = false };
        var arrangedPlan = await library.PlanSortAsync(b.Document.Id, arranged);
        check(arrangedPlan.DestinationPath == "Investments/77-1200/2026/2026-11 Oak Brokerage.txt", "a document's own arrangement shapes its folder and name");
        check((await library.PlanSortAsync(b.Document.Id, arranged with { NamePattern = "{date" })).Problem != null, "a broken arrangement is shown as a problem, not filed");
        await library.FileSortedAsync(b.Document.Id, arranged, b.Document.ContentHash, actor);
        var kindNow = (await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Brokerage statement");
        var bDetails = (await library.DocumentDetailsAsync(b.Document.Id))!;
        check(kindNow.FolderPattern == "Investments/{from}/{account_number}" && !bDetails.FollowsFolder && !bDetails.FollowsName,
            "an arrangement for this document only leaves the kind alone, and the document stays where it was put");
        var c = await Next("scan-c.txt", text.Replace("October", "December"));
        var kept = arranged with { Date = new DateOnly(2026, 12, 31), Fields = new() { ["account_number"] = "77-1200", ["statement_end_date"] = "2026-12-31" }, KeepPatterns = true };
        await library.FileSortedAsync(c.Document.Id, kept, c.Document.ContentHash, actor);
        kindNow = (await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Brokerage statement");
        check(kindNow.FolderPattern == "Investments/{account_number}/{date:yyyy}" && kindNow.NamePattern == "{statement_end_date:yyyy-MM} {from}" && (await library.DocumentDetailsAsync(c.Document.Id))!.FollowsFolder,
            "Use for every kind saves the arrangement to the kind on approval, and the document follows it");
        check((await library.RefilePlanAsync(kindId: kindNow.Id)).Any(x => x.EntryId == entry.Id), "documents that followed the old arrangement show up to move");
        var d = await Next("scan-d.txt", "Synthetic garden club newsletter, spring 2026");
        await library.FileSortedAsync(d.Document.Id, d.Choice with { Kind = "Newsletter", From = "", Title = "Garden club", FolderPattern = "Newsletters", NamePattern = "{title} {date:yyyy}", Date = new DateOnly(2026, 3, 1) }, d.Document.ContentHash, actor);
        var newsletter = (await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Newsletter");
        check(newsletter.FolderPattern == "Newsletters" && newsletter.NamePattern == "{title} {date:yyyy}" && File.Exists(Path.Combine(scratch, "Newsletters/Garden club 2026.txt")),
            "a new kind is made with the arrangement its first document was filed with");
        await library.SetIntakeAsync(inbox, false);
    }

    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds); }
}
