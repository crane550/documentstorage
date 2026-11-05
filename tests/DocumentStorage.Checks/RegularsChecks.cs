using System.Text;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

// Regulars: a kind from one sender, remembered from approvals, filling cards from memory without the
// model and filing itself once trusted. Synthetic documents only.
internal static class RegularsChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool, string> check, Func<Func<Task>, string, Task> refuses)
    {
        // Date rules are pure.
        const string sample = "Cedar Bank\nStatement period October 1, 2026 to October 31, 2026\nAccount ending 1234";
        check(LibraryService.DateRules(new DateOnly(2026, 10, 31), "scan.txt", sample).SequenceEqual(["latest"]) && LibraryService.DateRules(new DateOnly(2026, 10, 1), "scan.txt", sample).Contains("first")
            && LibraryService.DateRules(null, "scan.txt", sample).SequenceEqual(["none"]) && LibraryService.DateRules(new DateOnly(2020, 1, 1), "scan.txt", sample).SequenceEqual(["unseen"]),
            "the way a date was picked from a document is recognised (latest, first, earliest, from the name)");
        check(LibraryService.ApplyDateRule("latest", "scan.txt", sample) == new DateOnly(2026, 10, 31) && LibraryService.ApplyDateRule("filename", "2026-11-05 scan.txt", sample) == new DateOnly(2026, 11, 5),
            "a remembered way of picking the date gives the date in a new document");

        Directory.CreateDirectory(scratch);
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock); const string actor = "regulars-owner";
        var root = await library.AddRootAsync("Synthetic regulars archive", scratch, false);
        var top = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var inbox = await library.CreateFolderAsync(top.Id, "Inbox"); await library.SetIntakeAsync(inbox, true);
        await library.SaveSenderAsync(Guid.Empty, "Willow Savings", [], actor);
        var settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = true; settings.Model = "synthetic-sorter";
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        var month = 0;
        string Statement(int m, string ending = "1234") => $"WILLOW SAVINGS\nStatement period {Month(m)} 1, 2026 to {Month(m)} {Days(m)}, 2026\nAccount ending {ending}\nSynthetic balance {m}0.00";
        static string Month(int m) => System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m);
        static int Days(int m) => DateTime.DaysInMonth(2026, m);
        async Task<SortCard> Arrive(string text)
        {
            var name = $"scan-{++month:000}.txt";
            await library.UploadAsync(inbox, name, new MemoryStream(Encoding.UTF8.GetBytes(text))); clock.Advance(6); await library.ScanAsync(); clock.Advance(6); await library.ScanAsync();
            return (await library.SortCardAsync((await library.BrowseAsync(inbox)).Items.Single(x => x.Name == name).Id))!;
        }
        async Task<SortCard> Read(SortCard card, int m)
        {
            await library.QueueSortingAsync(card.Document.Id, actor);
            await library.ProcessNextIdentificationAsync(new Model(_ => new("Bank statement", "", "Willow Savings", $"2026-{m:00}-{Days(m)}", "Savings statement", ["banking"], [new("account_ending", "1234", "ending 1234")], "high")));
            return (await library.SortCardAsync(card.Document.Id))!;
        }
        await library.SaveTagAsync(Guid.Empty, "banking", [], actor);
        // Three months read and approved as suggested.
        for (var m = 1; m <= 3; m++)
        {
            var card = await Read(await Arrive(Statement(m)), m);
            check(card.Sources["kind"] == "reader", $"month {m}: the reader fills in the card while there is too little to remember");
            await library.FileSortedAsync(card.Document.Id, card.Choice, card.Document.ContentHash, actor);
        }
        var regulars = await library.RegularsAsync();
        var willow = regulars.Single(x => x.Sender == "Willow Savings" && x.Kind == "Bank statement");
        check(willow.Approved == 3 && willow.Streak == 3 && willow.Remembered == 3 && !willow.Suggest, "each approval is remembered for the regular, and its run grows");

        // Month four: no reading needed.
        var april = await Arrive(Statement(4));
        check(april.Sources["kind"] == "memory" && april.Choice.Kind == "Bank statement" && april.Choice.From == "Willow Savings" && april.Choice.Date == new DateOnly(2026, 4, 30)
            && april.Choice.Fields.GetValueOrDefault("account_ending") == "1234" && april.Choice.Title == "Savings statement" && april.Choice.Tags.SequenceEqual(["banking"]) && april.Sure && april.JobId == null,
            "a regular's next document is filled in from memory without the model: kind, date picked the same way, unchanging fields checked against the text, title and tags");
        check(april.Notes[0].StartsWith("Filled in from the last 3 bank statements from Willow Savings") && april.Plan.Problem == null, "the card says it was filled in from memory");
        settings = (await library.IdentificationOverviewAsync()).Settings; settings.AutoIdentify = true; settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        await library.QueueAutomaticIdentificationAsync(0);
        await using (var db = await factory.CreateDbContextAsync())
            check(!await db.IdentificationJobs.AnyAsync(x => x.EntryId == april.Document.Id), "automatic reading skips a document memory is already sure of, saving model time");
        check(await library.AutoFileFromMemoryAsync() == 0 && File.Exists(Path.Combine(scratch, "Inbox", april.Document.Name)), "until it is trusted, a regular only fills in the card");
        var otherAccount = await Arrive(Statement(4, "9999"));
        check(otherAccount.Sources["kind"] == "memory" && !otherAccount.Sure && !otherAccount.Choice.Fields.ContainsKey("account_ending") && otherAccount.Notes.Any(x => x.Contains("account ending")),
            "a remembered field not found in the document is left out and the card waits for a look");
        await library.TrashEntryAsync(otherAccount.Document.Id);
        await library.FileSortedAsync(april.Document.Id, april.Choice, april.Document.ContentHash, actor);
        var may = await Arrive(Statement(5));
        await library.FileSortedAsync(may.Document.Id, may.Choice, may.Document.ContentHash, actor);
        willow = (await library.RegularsAsync()).Single(x => x.Id == willow.Id);
        check(willow.Streak == 5 && willow.Suggest, "after five right in a row Stash suggests letting the regular file itself");
        await library.SnoozeRegularAsync(willow.Id, actor);
        check(!(await library.RegularsAsync()).Single(x => x.Id == willow.Id).Suggest, "Not now holds the suggestion back for a while");

        // Trusted: it files itself, even with the reader switched off.
        await library.SetRegularAutoFileAsync(willow.Id, true, actor);
        settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = false; settings.AutoIdentify = false; settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        var june = await Arrive(Statement(6));
        var juneCard = await library.SortCardAsync(june.Document.Id);
        check(await library.AutoFileFromMemoryAsync() == 1 && !File.Exists(Path.Combine(scratch, "Inbox", june.Document.Name))
            && File.Exists(Path.Combine(scratch, juneCard!.Plan.DestinationPath)) && juneCard.Plan.FileName.Contains("2026-06 Willow Savings statement 1234"), "a trusted regular files itself from memory, with the reader off");
        check(!(await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Bank statement").AutoFile, "trusting one regular leaves the rest of its kind asking");
        var filed = await library.RecentFilingsAsync();
        check(filed[0].Automatic && !filed[0].Confirmed, "it shows in Filed as filed by itself");
        await library.ConfirmFilingAsync(filed[0].PlanId, actor);
        check((await library.RecentFilingsAsync())[0].Confirmed && (await library.RegularsAsync()).Single(x => x.Id == willow.Id).Approved == 6, "Looks right counts toward the regular's record");
        await refuses(() => library.ConfirmFilingAsync(filed.Last().PlanId, actor), "only something that filed itself can be confirmed");
        var july = await Arrive(Statement(7));
        await library.AutoFileFromMemoryAsync();
        var julyFiling = (await library.RecentFilingsAsync())[0];
        await library.UndoFilingAsync(julyFiling.PlanId, actor);
        willow = (await library.RegularsAsync()).Single(x => x.Id == willow.Id);
        check(!willow.AutoFile && willow.Streak == 0 && File.Exists(Path.Combine(scratch, "Inbox", july.Document.Name)), "undoing one it filed switches the regular back to asking");
        await library.SetRegularAutoFileAsync(willow.Id, true, actor);
        check(await library.AutoFileFromMemoryAsync() == 0, "an undone document is not filed again by itself");

        // Rebuilding from history, for catalogs filed before regulars existed.
        await using (var db = await factory.CreateDbContextAsync()) await db.Routes.ExecuteDeleteAsync();
        check(await library.RebuildRegularsAsync() > 0 && (await library.RegularsAsync()).Single(x => x.Sender == "Willow Savings").Approved >= 5, "regulars rebuild from earlier approved filings");
        check(await library.RebuildRegularsAsync() == 0, "rebuilding happens only once");

        // Merging senders carries the regular across.
        await library.SaveSenderAsync(Guid.Empty, "Willow Savings Bank", [], actor);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var kindId = await db.Kinds.Where(x => x.Name == "Bank statement").Select(x => x.Id).SingleAsync();
            var other = await db.Correspondents.Where(x => x.Name == "Willow Savings Bank").Select(x => x.Id).SingleAsync();
            db.Routes.Add(new() { KindId = kindId, CorrespondentId = other, Approved = 2, RecentJson = "[]" }); await db.SaveChangesAsync();
        }
        var before = (await library.RegularsAsync()).Single(x => x.Sender == "Willow Savings").Approved;
        var vocab = await library.SortVocabularyAsync();
        await library.MergeSendersAsync(vocab.Senders.Single(x => x.Name == "Willow Savings Bank").Id, vocab.Senders.Single(x => x.Name == "Willow Savings").Id, actor);
        check((await library.RegularsAsync()).Single(x => x.Sender == "Willow Savings").Approved == before + 2, "merging senders adds their regulars' records together");
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
