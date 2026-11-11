using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

// Working out what kind of document something is: asked openly first, matched to the person's
// kinds by what its name has led to before, and a new kind proposed when nothing fits.
// Synthetic documents and a scripted reader; this proves the steps, not any model's judgement.
internal static class ReadingChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool, string> check, Func<Func<Task>, string, Task> refuses)
    {
        // ---- The steps by themselves: no catalog ----
        ReadingKind Kind(string name, string description = "", bool catchAll = false) => new(name, description, [], new Dictionary<string, string>(), catchAll);
        ReadingExample Filed(string kind, string words, string text = "Synthetic earlier document") => new(kind, words, text, "", "", "", [], new Dictionary<string, string>());
        var kinds = new[] { Kind("Receipt", "Proof of a purchase already made."), Kind("Purchase order", "An order to buy goods."), Kind("Letter"), Kind("Everything else", "Anything else.", true) };
        var asked = new List<(string Step, string Document, string Question, string Schema)>();
        async Task<SortReply> Read(string text, ReadingKnowledge know, string type, string nearest = "", bool same = false, string about = "Synthetic documents of this type.")
        {
            asked.Clear();
            var result = await DocumentReading.RunAsync(new("Scan 1.txt", "Inbox", text), know, (step, document, question, schema, _) =>
            {
                asked.Add((step, document, question, JsonSerializer.Serialize(schema)));
                return Task.FromResult(step switch
                {
                    "describe" => JsonSerializer.Serialize(new { signs = new[] { "not in the text", text.Split('\n')[0] }, type, about }),
                    "match" => JsonSerializer.Serialize(new { nearest, reason = "Synthetic.", same }),
                    _ => """{"from":"","date":"","title":"Synthetic","tags":[],"fields":[],"confidence":"low"}"""
                });
            });
            return result.Reply;
        }
        var nothing = new ReadingKnowledge(kinds, [], [], [], "");
        var reply = await Read("THE COPPER KETTLE\nTotal 33.71 paid", nothing, "receipt");
        check(reply.Kind == "Receipt" && reply.KindBasis == KindBasis.Named && asked.Select(x => x.Step).SequenceEqual(["describe", "details"]) && reply.What == "receipt",
            "a document the reader names as one of the person's kinds needs no second opinion");
        check(!kinds.Any(k => asked[0].Question.Contains(k.Name)) && reply.Signs!.SequenceEqual(["THE COPPER KETTLE"]),
            "what it is gets asked with none of the person's kinds on show, and only signs that are really in the document are kept");
        check(asked.All(x => x.Document == asked[0].Document) && asked[0].Document.StartsWith("DOCUMENT\nFile name: Scan 1.txt\nArrived in folder: Inbox\n", StringComparison.Ordinal),
            "every question of a reading starts with the same document, so a local server reads it once");

        reply = await Read("Thanks for your order! Payment received 39.74", nothing, "Order confirmation", nearest: "Receipt", same: true);
        check(reply.Kind == "Receipt" && reply.KindBasis == KindBasis.Chosen && !KindBasis.Settled(reply.KindBasis) && asked.Select(x => x.Step).SequenceEqual(["describe", "match", "details"]),
            "a name that is new here is matched against the person's kinds, and the choice waits for a person");
        var match = asked.Single(x => x.Step == "match");
        check(match.Question.Contains("- Receipt: Proof of a purchase already made.") && match.Question.Contains("- Purchase order") && !match.Question.Contains("Everything else") && !match.Schema.Contains("Everything else"),
            "the reader chooses among the person's kinds with their descriptions, never the catch-all");

        reply = await Read("Evening on the Millpond\nfor piano\nAndante", nothing, "Sheet music", nearest: "Letter", same: false, about: "Printed musical notation for performers.");
        check(reply.Kind == "Sheet music" && reply.KindBasis == KindBasis.New && reply.KindDescription == "Printed musical notation for performers." && reply.KindOptions!.SequenceEqual(["Letter"]),
            "a document that belongs in none of the kinds is given a new kind under the reader's own name, with the nearest kind kept as an option");
        check(asked.Single(x => x.Step == "details").Question.Contains("filed as \"Sheet music\"") && asked.Single(x => x.Step == "details").Question.Contains("up to four values worth keeping"),
            "a new kind has no fields yet, so the reader may suggest a few");

        // What the reader called a document, beside what the person filed it as, is all that is learned.
        var learned = nothing with { Examples = [Filed("Receipt", "Order confirmation")] };
        reply = await Read("Thanks for your order!", learned, "order confirmations");
        check(reply.Kind == "Receipt" && reply.KindBasis == KindBasis.Named && asked.Count == 2, "a name the person has filed as one kind before leads straight to that kind");
        reply = await Read("Synthetic unrelated", learned, "Warranty card", nearest: "Receipt", same: true);
        check(asked.Single(x => x.Step == "match").Question.Contains("- Receipt: Proof of a purchase already made. Also called: Order confirmation."),
            "when matching a new name, each kind shows the other names the reader has used for it");

        var split = nothing with { Examples = [Filed("Receipt", "Order confirmation", "BRIGHTCART Payment received 39.74"), Filed("Purchase order", "Order confirmation", "We confirm your order to supply 500 screws")] };
        reply = await Read("Northwind Outfitters ORDER RECEIPT Total charged 50.47", split, "Order confirmation", nearest: "Receipt", same: true);
        match = asked.Single(x => x.Step == "match");
        check(reply.Kind == "Receipt" && reply.KindBasis == KindBasis.Chosen && reply.KindOptions!.SequenceEqual(["Purchase order"]) && !match.Question.Contains("- Letter")
            && match.Question.Contains("BRIGHTCART Payment received") && match.Question.Contains("We confirm your order to supply"),
            "a name that has led to two kinds is no longer trusted: the reader chooses between just those, shown one of each, and the other stays an option");

        var mistaken = nothing with { Examples = [Filed("Receipt", "Purchase order", "GREENLEAF MARKET Subtotal 12.54 Thank you")] };
        reply = await Read("CEDAR HOLLOW HARDWARE Subtotal 20.86 Thank you", mistaken, "Purchase order", nearest: "Receipt", same: true);
        check(reply.Kind == "Receipt" && reply.KindBasis == KindBasis.Chosen && asked.Count == 3 && asked.Single(x => x.Step == "match").Question.Contains("GREENLEAF MARKET"),
            "once a person corrects a document the reader called by a kind's own name, that name is questioned next time instead of trusted");
        reply = await Read("Synthetic", mistaken, "Purchase order", nearest: "Purchase order", same: false);
        check(reply.Kind == "Purchase order" && reply.KindBasis == KindBasis.Chosen, "a kind's own name never becomes a second kind of the same name");

        var steady = nothing with { Examples = [.. Enumerable.Range(0, 3).Select(_ => Filed("Receipt", "Sales slip")), Filed("Letter", "Sales slip")] };
        reply = await Read("Synthetic", steady, "Sales slip");
        check(reply.Kind == "Receipt" && reply.KindBasis == KindBasis.Named && reply.KindOptions!.SequenceEqual(["Letter"]) && asked.Count == 2,
            "one stray filing does not unsettle a name that nearly always leads to the same kind");
        check(DocumentReading.WhereNameLed("Sales slips", steady).Select(x => (x.Kind.Name, x.Count)).SequenceEqual([("Receipt", 3), ("Letter", 1)]), "names count the same whatever their plural or capitals");

        var longText = string.Join('\n', Enumerable.Range(0, 900).Select(i => $"Synthetic line {i:0000}"));
        var block = DocumentReading.DocumentBlock(new("long.txt", "", longText, 2000));
        check(block.Contains("Synthetic line 0000") && block.Contains("Synthetic line 0899") && block.Contains("characters in the middle are not shown") && !block.Contains("Synthetic line 0400") && block.Length < 2300,
            "a long document is shown by its start and its end, within the budget");
        var silent = false;
        try { await Read("Synthetic", nothing, "  "); } catch (OllamaException) { silent = true; }
        check(silent, "a reading that doesn't say what the document is fails instead of guessing");

        // ---- Through the catalog: what approval teaches, and what it doesn't ----
        Directory.CreateDirectory(scratch);
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock); const string actor = "reading-owner";
        var root = await library.AddRootAsync("Synthetic reading archive", scratch, false);
        var top = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var inbox = await library.CreateFolderAsync(top.Id, "Inbox"); await library.SetIntakeAsync(inbox, true);
        var settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = true; settings.Model = "synthetic-reader";
        await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        async Task<SortCard> Arrive(string name, string text)
        {
            await library.UploadAsync(inbox, name, new MemoryStream(Encoding.UTF8.GetBytes(text))); clock.Advance(6); await library.ScanAsync(); clock.Advance(6); await library.ScanAsync();
            return (await library.SortCardAsync((await library.BrowseAsync(inbox)).Items.Single(x => x.Name == name).Id))!;
        }
        ScriptedModel model = null!;
        async Task<SortCard> ReadCard(SortCard card, SortReply answer)
        {
            await library.QueueSortingAsync(card.Document.Id, actor);
            model = new ScriptedModel(_ => answer);
            await library.ProcessNextIdentificationAsync(model);
            return (await library.SortCardAsync(card.Document.Id))!;
        }
        async Task<DocumentDetail> Detail(Guid id) { await using var db = await factory.CreateDbContextAsync(); return await db.Details.AsNoTracking().SingleAsync(x => x.EntryId == id); }
        string Score(int n) => $"Maple Falls Synthetic Choir\nHymn of the Harvest number {n}\nSATB with organ\nMarch {n}, 2026\nSoprano Alto Tenor Bass";
        SortReply Says(string day, string what = "Synthetic choir score") => new("Synthetic sheet music", "Printed music for the synthetic choir.", "Maple Falls Synthetic Choir", $"2026-03-{day}", "Harvest hymn", [], [], "high", What: what);

        var first = await ReadCard(await Arrive("Scan 31.txt", Score(1)), Says("01"));
        check(first.Plan.NewKind && first.Choice.Kind == "Synthetic choir score" && first.ReaderWords == "Synthetic choir score" && first.Sources["kind"] == "guess" && !first.Sure
            && model.Asked.Select(x => x.Step).SequenceEqual(["describe", "match", "details"]),
            "a document unlike any of the kinds arrives as a new kind under the reader's own name, pencilled in for a person");
        using (var steps = JsonDocument.Parse((await Job(first.JobId!.Value)).ContextJson))
            check(steps.RootElement.GetArrayLength() == 3 && steps.RootElement[0].GetProperty("step").GetString() == "describe" && steps.RootElement[0].GetProperty("answer").GetString()!.Contains("Synthetic choir score"),
                "each question asked and its answer is kept with the reading");
        // The person names the kind their own way. Nothing but that pairing is kept.
        await library.FileSortedAsync(first.Document.Id, first.Choice with { Kind = "Synthetic sheet music" }, first.Document.ContentHash, actor);
        var kept = await Detail(first.Document.Id);
        check(kept.Confirmed && kept.ReaderWords == "Synthetic choir score" && (await library.SortVocabularyAsync()).Kinds.Any(x => x.Name == "Synthetic sheet music"),
            "approving keeps what the reader called the document beside the kind the person filed it as");

        var music = (await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Synthetic sheet music");
        await library.SetKindAutoFileAsync(music.Id, true, actor);
        var second = await ReadCard(await Arrive("Scan 32.txt", Score(2)), Says("02"));
        check(model.Asked.Select(x => x.Step).SequenceEqual(["describe", "details"]) && File.Exists(Path.Combine(scratch, "Synthetic sheet music/Maple Falls Synthetic Choir/2026-03-02 Maple Falls Synthetic Choir Harvest hymn.txt")),
            "the next one the reader calls the same thing is known without asking, and a trusted kind files it");
        check(!(await Detail(second.Document.Id)).Confirmed, "a document that filed itself is not yet something readings learn from");
        var filedItself = (await library.RecentFilingsAsync()).First(x => x.EntryId == second.Document.Id);
        await library.ConfirmFilingAsync(filedItself.PlanId, actor);
        check((await Detail(second.Document.Id)).Confirmed, "saying it looks right makes it count");
        await library.SetKindAutoFileAsync(music.Id, false, actor);

        // The same words for something the person files elsewhere: the name stops being trusted.
        var third = await ReadCard(await Arrive("Scan 33.txt", "Maple Falls Synthetic Choir\nMarch 3, 2026\nDear singers, rehearsal moves to Thursday."), Says("03"));
        check(third.Choice.Kind == "Synthetic sheet music" && third.Sources["kind"] == "reader", "until the person says otherwise the learned name stands");
        await library.FileSortedAsync(third.Document.Id, third.Choice with { Kind = "Letter" }, third.Document.ContentHash, actor);
        var fourth = await ReadCard(await Arrive("Scan 34.txt", Score(4)), Says("04"));
        var between = model.Asked.Single(x => x.Step == "match");
        check(fourth.Choice.Kind == "Synthetic sheet music" && fourth.Sources["kind"] == "guess" && !fourth.Sure && fourth.OtherKinds.SequenceEqual(["Letter"])
            && between.Question.Contains("Dear singers") && between.Question.Contains("Hymn of the Harvest") && !between.Question.Contains("- Invoice"),
            "after the correction, the reader is asked to choose between the two kinds that name has led to, and the slip offers the other with one click");
        check(fourth.Notes.Any(x => x.Contains("“Synthetic choir score”") && x.Contains("check the kind")), "the slip says what the reader called it and that the kind needs a look");

        // Undo takes a filing back out of what readings learn from.
        var undo = (await library.RecentFilingsAsync()).First(x => x.EntryId == third.Document.Id);
        await library.UndoFilingAsync(undo.PlanId, actor);
        check(!(await Detail(third.Document.Id)).Confirmed, "an undone filing no longer teaches");
        var fifth = await ReadCard(await Arrive("Scan 35.txt", Score(5)), Says("05"));
        check(fifth.Sources["kind"] == "reader" && model.Asked.Count == 2, "with the stray filing undone, the name is trusted again");
        await library.SetIntakeAsync(inbox, false);

        async Task<IdentificationJob> Job(Guid id) { await using var db = await factory.CreateDbContextAsync(); return await db.IdentificationJobs.AsNoTracking().SingleAsync(x => x.Id == id); }
    }

    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds); }
}
