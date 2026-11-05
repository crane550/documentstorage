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
        SortRequest? sent = null;
        await library.ProcessNextIdentificationAsync(new Model(input => { sent = input; return new("Brokerage statement", "", "Oak Brokerage", "2026-10-31", "Brokerage statement", [],
            [new("statement_end_date", "October 31, 2026", "ending October 31, 2026"), new("account_number", "77-1200", "Account number 77-1200")], "high"); }));
        using (var prompt = JsonDocument.Parse(sent!.PromptJson))
            check(prompt.RootElement.GetProperty("known_kinds").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Brokerage statement").GetProperty("fields").EnumerateArray().Select(x => x.GetString()).Contains("statement_end_date"),
                "a kept field is asked of the reader from then on");
        var card = (await library.SortCardAsync(entry.Id))!;
        check(card.Choice.Fields["statement_end_date"] == "2026-10-31" && card.Plan.FolderPath == "Investments/Oak Brokerage/77-1200" && card.Plan.FileName == "2026-10-31 Oak Brokerage statement 2026-10-31.txt",
            "a written-out date from the reader is stored as yyyy-MM-dd, and kept fields fill in the folder and name");
        var oneOff = card.Choice with { Fields = new(card.Choice.Fields) { ["advisor"] = "Synthetic Advisor" } };
        await library.FileSortedAsync(entry.Id, oneOff, card.Document.ContentHash, actor);
        var saved = (await library.DocumentDetailsAsync(entry.Id))!;
        check(saved.Fields["advisor"] == "Synthetic Advisor" && !LibraryService.KindFields((await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Brokerage statement")).Contains("advisor"),
            "a field added on one document is saved on that document without changing the kind");
        await library.SetIntakeAsync(inbox, false);
    }

    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds); }
    private sealed class Model(Func<SortRequest, SortReply> answer) : IOllamaClient
    {
        public Task<List<LocalModel>> ModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(new List<LocalModel>());
        public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Sorting never asks the old question.");
        public Task<SortReply> SortAsync(SortRequest request, CancellationToken cancellationToken = default) => Task.FromResult(answer(request));
    }
}
