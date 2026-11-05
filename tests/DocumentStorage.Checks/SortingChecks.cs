using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

// Kinds, senders, tags and details; the reader's narrow answer; folders and names built in code;
// approval, undo, learning and kinds that file themselves. Synthetic documents only.
internal static class SortingChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool, string> check, Func<Func<Task>, string, Task> refuses)
    {
        // ---- Pure pieces ----
        var values = new SortValues("Bank statement", "Cedar Bank", new DateOnly(2026, 10, 31), "Checking statement", new Dictionary<string, string> { ["account_ending"] = "1234" });
        check(string.Join('/', SortPatterns.Folder("Banking/{from}/{date:yyyy}", values)) == "Banking/Cedar Bank/2026", "folder patterns fill in sender and year");
        check(SortPatterns.Name("{date:yyyy-MM} {from} statement {account_ending}", values, "20261105_143022.pdf") == "2026-10 Cedar Bank statement 1234.pdf", "name patterns keep the original extension");
        var empty = values with { From = "", Date = null };
        check(string.Join('/', SortPatterns.Folder("Banking/{from}/{date:yyyy}", empty)) == "Banking", "a missing sender or date leaves its folder level out instead of an empty name");
        check(SortPatterns.Name("{date} - {from} - {title}", empty, "scan.pdf") == "Checking statement.pdf", "missing name parts leave no dangling separators");
        var hostile = values with { From = "../../etc", Title = "a/b\\c:d" };
        var segments = SortPatterns.Folder("{from}/{title}", hostile);
        check(segments.All(x => !x.Contains('/') && !x.Contains('\\') && x != ".." && x != ".") && segments[0] == "etc", "sender and title text can never become path separators or step out of the folder");
        await refuses(() => Task.Run(() => SortPatterns.ValidatePattern("Banking/{from", true)), "an unclosed pattern token is refused");
        await refuses(() => Task.Run(() => SortPatterns.ValidatePattern("Banking/{FROM}", true)), "an unknown pattern token is refused");
        await refuses(() => Task.Run(() => SortPatterns.ValidatePattern("../outside/{from}", true)), "a pattern cannot step outside the storage location");
        await refuses(() => Task.Run(() => SortPatterns.ValidatePattern("{date}/{title}", false)), "a name pattern cannot hold folders");
        check(!DocumentHints.IsDescriptive("20261105_143022.pdf") && !DocumentHints.IsDescriptive("Scan 12.pdf") && !DocumentHints.IsDescriptive("IMG_0042.PDF") && !DocumentHints.IsDescriptive("doc04012620261105.pdf"),
            "scanner names are recognised as saying nothing");
        check(DocumentHints.IsDescriptive("Acme_Invoice_INV-20391_2026-10-14.pdf") && DocumentHints.IsDescriptive("Truck title.pdf"), "emailed names that describe the document are recognised");
        var dates = DocumentHints.FindDates("Statement period ending October 31, 2026. Printed 11/02/2026. Ref 2026-10-15.", "text").Select(x => x.Date).ToList();
        check(dates.Contains(new(2026, 10, 31)) && dates.Contains(new(2026, 11, 2)) && dates.Contains(new(2026, 10, 15)), "dates are found in written, US and ISO forms");
        check(DocumentHints.FindDates("Acme_Invoice_20261014", "filename").Single().Date == new DateOnly(2026, 10, 14), "a compact date in a filename is read");
        var acme = new NamedTerm(Guid.NewGuid(), "Acme Supply", ["ACME Supply Co"]);
        var hints = DocumentHints.Read("Invoice 4471.pdf", "ACME SUPPLY CO\nInvoice 4471", [new(Guid.NewGuid(), "Invoice", "A request for payment", ["invoice_number"], false)], [acme], []);
        check(hints.Senders.SequenceEqual([acme.Id]) && hints.Kinds.Count == 1 && hints.Kinds[0].Score >= 10, "known senders (by any spelling) and a kind named in the filename are found without the model");
        var reply = OllamaClient.ParseSort("""{"kind":"Invoice","kind_description":"","from":"Acme","date":"2026-10-14","title":"Shop supplies","tags":["shop","shop"],"fields":[{"name":"invoice_number","value":"INV-1","quote":"INV-1"}],"confidence":"certain","extra":"ignored"}""");
        check(reply.Confidence == "low" && reply.Tags.Length == 1 && reply.Fields.Length == 1, "a reading is bounded and an unknown confidence counts as low");
        await refuses(() => Task.Run(() => { try { OllamaClient.ParseSort("""{"kind":""}"""); } catch (OllamaException ex) { throw new StorageException(ex.Message); } }), "a reading without a kind is refused");

        // A Word document's text, without any outside tool.
        Directory.CreateDirectory(scratch);
        var docx = Path.Combine(scratch, "letter.docx");
        using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
        {
            await using var part = zip.CreateEntry("word/document.xml").Open();
            await part.WriteAsync(Encoding.UTF8.GetBytes("""<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Dear neighbour,</w:t></w:r></w:p><w:p><w:r><w:t>Synthetic</w:t><w:tab/><w:t xml:space="preserve"> fence letter</w:t></w:r></w:p></w:body></w:document>"""));
        }
        var docText = await new DocumentTextReader().ReadAsync(docx);
        check(docText.Status == "Indexed" && docText.Text.Contains("Dear neighbour,") && docText.Text.Contains("Synthetic\t fence letter"), "Word documents are read, tabs and paragraphs kept");
        File.Delete(docx);

        // ---- The pipeline ----
        var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock); const string actor = "sorting-owner";
        var root = await library.AddRootAsync("Synthetic sorting archive", scratch, false);
        var top = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var inbox = await library.CreateFolderAsync(top.Id, "Inbox"); await library.SetIntakeAsync(inbox, true);
        var banking = await library.CreateFolderAsync(top.Id, "banking");
        var vocab = await library.SortVocabularyAsync();
        check(vocab.Kinds.Count >= 10 && vocab.Kinds.Count(x => x.IsCatchAll) == 1 && vocab.Kinds.Any(x => x.Name == "Purchase order"), "a starter list of kinds is ready, with one catch-all");
        await library.SaveSenderAsync(Guid.Empty, "Cedar Bank", ["Cedar Bank N.A."], actor);
        await library.SaveTagAsync(Guid.Empty, "house", [], actor);
        var settings = (await library.IdentificationOverviewAsync()).Settings; settings.Enabled = true; settings.Model = "synthetic-sorter";
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        var count = 0;
        async Task<SortCard> Arrive(string name, string text)
        {
            await library.UploadAsync(inbox, name, new MemoryStream(Encoding.UTF8.GetBytes(text))); clock.Advance(6); await library.ScanAsync(); clock.Advance(6); await library.ScanAsync();
            var entry = (await library.BrowseAsync(inbox)).Items.Single(x => x.Name == name); count++;
            return (await library.SortCardAsync(entry.Id))!;
        }
        SortRequest? sent = null;
        async Task<SortCard> Read(SortCard card, SortReply answer)
        {
            await library.QueueSortingAsync(card.Document.Id, actor);
            await library.ProcessNextIdentificationAsync(new Model(input => { sent = input; return answer; }));
            return (await library.SortCardAsync(card.Document.Id))!;
        }
        const string statement = "CEDAR BANK N.A.\nChecking account ending 1234\nStatement period October 1, 2026 to October 31, 2026\nSynthetic balance 42.50";
        var scan = await Arrive("20261105_143022.txt", statement);
        check(!scan.DescriptiveName && scan.Choice.From == "Cedar Bank" && scan.Choice.Kind == "" && scan.Plan.Problem != null, "without the reader, a scan's card starts with what plain matching finds and asks for a kind");
        scan = await Read(scan, new("Bank statement", "", "CEDAR BANK N.A.", "2026-10-31", "Checking statement", ["house", "banking-2026", "another-new"],
            [new("account_ending", "1234", "ending 1234"), new("account_ending_bad", "9", "not in text")], "high"));
        using (var prompt = JsonDocument.Parse(sent!.PromptJson))
        {
            var p = prompt.RootElement;
            check(p.GetProperty("known_kinds").GetArrayLength() >= 10 && p.GetProperty("known_senders").EnumerateArray().Any(x => x.GetString() == "Cedar Bank") && !sent.PromptJson.Contains(scratch)
                && !p.TryGetProperty("folders", out _) && p.GetProperty("text").GetString()!.Length <= LibraryService.SortTextBudget + 60,
                "the reader is asked a narrow question: kinds, senders, tags and the first page, with no folders or server paths");
        }
        check(scan.Choice.Kind == "Bank statement" && scan.Choice.From == "Cedar Bank" && scan.Choice.Date == new DateOnly(2026, 10, 31) && scan.Sources["date"] == "reader",
            "the reader's sender spelling is mapped to the known sender and its date is checked against the text");
        check(scan.Choice.Tags.SequenceEqual(["house", "banking-2026"]) && scan.Choice.Fields.Keys.SequenceEqual(["account_ending"]), "at most one new tag is taken, and only fields of the kind with evidence in the text");
        check(scan.Plan.Problem == null && scan.Plan.FolderPath == "banking/Cedar Bank/2026" && scan.Plan.NewFolders.SequenceEqual(["Cedar Bank", "2026"]) && scan.Plan.FileName == "2026-10 Cedar Bank statement 1234.txt",
            "the folder and name come from the kind's patterns, reusing an existing folder whatever its letter case and planning the new ones");
        check(!scan.Sure, "a new tag keeps a reading from counting as sure");
        await library.CreateFolderAsync(top.Id, "Something new");
        check((await library.SortCardAsync(scan.Document.Id))!.ReaderState == "Read", "changing folders does not throw away a slow reading");
        await refuses(() => library.FileSortedAsync(scan.Document.Id, scan.Choice, "stale-hash", actor), "filing is refused when the document has changed since it was looked at");
        var opId = await library.FileSortedAsync(scan.Document.Id, scan.Choice, scan.Document.ContentHash, actor);
        var filedPath = Path.Combine(scratch, "banking/Cedar Bank/2026/2026-10 Cedar Bank statement 1234.txt");
        check(File.Exists(filedPath) && await File.ReadAllTextAsync(filedPath) == statement && !File.Exists(Path.Combine(scratch, "Inbox/20261105_143022.txt")), "approval creates the folders and moves the unchanged bytes");
        var details = (await library.DocumentDetailsAsync(scan.Document.Id))!;
        check(details.Kind == "Bank statement" && details.From == "Cedar Bank" && details.Date == new DateOnly(2026, 10, 31) && details.Tags.SequenceEqual(["banking-2026", "house"]) && details.Fields["account_ending"] == "1234",
            "the details are saved on the document and stay with it after filing");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var op = await db.Operations.SingleAsync(x => x.Id == opId);
            var kind = await db.Kinds.SingleAsync(x => x.Name == "Bank statement");
            check(op.State == "Completed" && op.Kind == "Organize" && kind.Approved == 1 && kind.Streak == 1 && kind.Corrected == 0, "filing goes through the journal and counts toward the kind's record");
            check(await db.FilingExamples.AnyAsync(x => x.EntryId == scan.Document.Id && x.DocumentType == "Bank statement"), "the approval is remembered as an example for later readings");
            check(await db.Entries.CountAsync(x => x.RootId == root && x.IsFolder && (x.RelativePath == "banking/Cedar Bank" || x.RelativePath == "banking/Cedar Bank/2026")) == 2, "the new folders are in the catalog");
        }

        // A second statement whose name is taken: the person corrects the sender; the reader's spelling is learned.
        var second = await Arrive("Scan 2.txt", statement.Replace("CEDAR BANK N.A.", "Cedar Bank and Trust"));
        second = await Read(second, new("Bank statements", "", "Cedar Bank and Trust", "2026-10-31", "Checking statement", [], [new("account_ending", "1234", "ending 1234")], "high"));
        check(second.Choice.From == "Cedar Bank" && second.Choice.Kind == "Bank statement", "a plural kind and a longer sender name still match what Stash knows");
        var corrected = second.Choice with { From = "Cedar Credit Union" };
        var plan = await library.PlanSortAsync(second.Document.Id, corrected);
        check(plan.NewSender && plan.FolderPath == "banking/Cedar Credit Union/2026", "the path follows a correction before anything is filed");
        var sameName = await library.PlanSortAsync(second.Document.Id, second.Choice);
        check(sameName.FileName == "2026-10 Cedar Bank statement 1234 (2).txt", "a name already taken gets a number, never an overwrite");
        await library.FileSortedAsync(second.Document.Id, corrected, second.Document.ContentHash, actor);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var kind = await db.Kinds.SingleAsync(x => x.Name == "Bank statement");
            var credit = await db.Correspondents.SingleAsync(x => x.Name == "Cedar Credit Union");
            check(kind.Approved == 2 && kind.Corrected == 1 && kind.Streak == 0, "a correction resets the kind's run of approvals");
            check(LibraryService.Aliases(credit.AliasesJson).Contains("Cedar Bank and Trust"), "the reader's spelling is remembered for the sender the person chose");
        }

        // An emailed document keeps its descriptive name, and a new kind is made on approval.
        var emailed = await Arrive("Fence permit approval letter.txt", "City of Synthetic Falls\nBuilding department\nPermit 88-12 approved March 3, 2026");
        emailed = await Read(emailed, new("Permit", "Building and planning permits.", "City of Synthetic Falls", "2026-03-03", "Fence permit", [], [], "medium"));
        check(emailed.DescriptiveName && emailed.Choice.KeepName && emailed.Plan.FileName == "Fence permit approval letter.txt" && emailed.Plan.NewKind && emailed.Plan.FolderPath == "Permit/City of Synthetic Falls",
            "a descriptive emailed name is kept, and a new kind files under its own name until its patterns are set");
        await library.FileSortedAsync(emailed.Document.Id, emailed.Choice, emailed.Document.ContentHash, actor);
        vocab = await library.SortVocabularyAsync();
        var permit = vocab.Kinds.Single(x => x.Name == "Permit");
        check(permit.Description == "Building and planning permits." && permit.FolderPattern == LibraryService.DefaultKindFolder && vocab.Senders.Any(x => x.Name == "City of Synthetic Falls"),
            "approving creates the new kind with the reader's description, and the new sender");

        // Undo puts it back, with its details, so the card returns filled in.
        var filed = await library.RecentFilingsAsync();
        check(filed.Count == 3 && filed[0].EntryId == emailed.Document.Id && filed.Single(x => x.EntryId == second.Document.Id).Corrected, "the Filed list shows what went where and which were corrected");
        await library.UndoFilingAsync(filed[0].PlanId, actor);
        check(File.Exists(Path.Combine(scratch, "Inbox/Fence permit approval letter.txt")) && (await library.RecentFilingsAsync()).Count == 2, "undo moves a filed document back to the inbox");
        var back = (await library.SortCardAsync(emailed.Document.Id))!;
        check(back.Choice.Kind == "Permit" && back.Sources["kind"] == "person", "an undone document comes back with the details it was filed with");
        await refuses(() => library.UndoFilingAsync(filed[0].PlanId, actor), "a filing cannot be undone twice");

        // Kinds that file themselves.
        var statementKind = vocab.Kinds.Single(x => x.Name == "Bank statement");
        await library.SetKindAutoFileAsync(statementKind.Id, true, actor);
        var auto = await Arrive("Scan 3.txt", statement.Replace("October 31, 2026", "November 30, 2026").Replace("October 1, 2026", "November 1, 2026"));
        auto = await Read(auto, new("Bank statement", "", "Cedar Bank", "2026-11-30", "Checking statement", ["house"], [new("account_ending", "1234", "ending 1234")], "high"));
        var autoPath = Path.Combine(scratch, "banking/Cedar Bank/2026/2026-11 Cedar Bank statement 1234.txt");
        check(File.Exists(autoPath), "a sure reading of a kind switched on files itself");
        filed = await library.RecentFilingsAsync();
        check(filed[0].Automatic && filed[0].Actor == "automatic", "a filing it did by itself is marked as such in the Filed list");
        var unsure = await Arrive("Scan 4.txt", statement);
        unsure = await Read(unsure, new("Bank statement", "", "Cedar Bank", "2026-12-31", "Checking statement", [], [new("account_ending", "1234", "ending 1234")], "high"));
        check(File.Exists(Path.Combine(scratch, "Inbox/Scan 4.txt")) && unsure.Sources["date"] == "guess" && !unsure.Sure, "a date not written in the document keeps it waiting for a person");
        var medium = await Arrive("Scan 5.txt", statement);
        await Read(medium, new("Bank statement", "", "Cedar Bank", "2026-10-31", "Checking statement", [], [new("account_ending", "1234", "ending 1234")], "medium"));
        check(File.Exists(Path.Combine(scratch, "Inbox/Scan 5.txt")), "a reading the model wasn't sure of waits for a person");
        await library.SetAutoFilingEnabledAsync(false, actor);
        var paused = await Arrive("Scan 6.txt", statement.Replace("October 31, 2026", "December 31, 2026"));
        await Read(paused, new("Bank statement", "", "Cedar Bank", "2026-12-31", "Checking statement", [], [new("account_ending", "1234", "ending 1234")], "high"));
        check(File.Exists(Path.Combine(scratch, "Inbox/Scan 6.txt")), "the master switch stops all automatic filing");
        await library.SetAutoFilingEnabledAsync(true, actor);
        await library.UndoFilingAsync(filed[0].PlanId, actor);
        vocab = await library.SortVocabularyAsync();
        check(!vocab.Kinds.Single(x => x.Name == "Bank statement").AutoFile, "undoing an automatic filing switches its kind back to asking first");

        // Library details without moving, and merging senders.
        await library.SaveDocumentDetailsAsync(scan.Document.Id, new("Bank statement", "Cedar Bank", new(2026, 10, 31), "Checking statement", ["house", "taxes"], new() { ["account_ending"] = "1234" }), actor);
        check(File.Exists(filedPath) && (await library.DocumentDetailsAsync(scan.Document.Id))!.Tags.Contains("taxes"), "details can be changed in the Library without moving the file");
        vocab = await library.SortVocabularyAsync();
        var bank = vocab.Senders.Single(x => x.Name == "Cedar Bank"); var creditUnion = vocab.Senders.Single(x => x.Name == "Cedar Credit Union");
        await library.MergeSendersAsync(creditUnion.Id, bank.Id, actor);
        check((await library.DocumentDetailsAsync(second.Document.Id))!.From == "Cedar Bank" && LibraryService.Aliases((await library.SortVocabularyAsync()).Senders.Single(x => x.Id == bank.Id).AliasesJson).Contains("Cedar Credit Union"),
            "merging senders moves their documents and keeps the old name as a spelling");

        // ---- The fluid tree: re-filing when patterns or senders change ----
        // The merge above moved the second statement's sender to Cedar Bank, so its folder and name are out of date.
        var afterMerge = await library.RefilePlanAsync(senderId: bank.Id);
        var mergedItem = afterMerge.SingleOrDefault(x => x.EntryId == second.Document.Id);
        check(mergedItem != null && mergedItem.FromPath == "banking/Cedar Credit Union/2026/2026-10 Cedar Credit Union statement 1234.txt"
            && mergedItem.ToPath == "banking/Cedar Bank/2026/2026-10 Cedar Bank statement 1234 (2).txt", "merging senders lists the documents whose folder and name follow the old name");
        check(afterMerge.All(x => x.EntryId != emailed.Document.Id), "documents still in an inbox are left to the Inbox");
        vocab = await library.SortVocabularyAsync();
        var statementKindNow = vocab.Kinds.Single(x => x.Name == "Bank statement");
        statementKindNow.FolderPattern = "Banking/{date:yyyy}/{from}";
        await library.SaveKindAsync(statementKindNow, actor);
        var moves = await library.RefilePlanAsync(kindId: statementKindNow.Id);
        check(moves.Count == 2 && moves.Select(x => x.ToPath).Distinct().Count() == 2 && moves.All(x => x.NewFolders.SequenceEqual(["2026", "Cedar Bank"]) && x.ToPath.StartsWith("banking/2026/Cedar Bank/2026-10 Cedar Bank statement 1234")),
            "changing a kind's folder pattern lists where each of its documents would go, with the folders it would make");
        check(File.Exists(filedPath), "listing moves changes nothing on disk");
        var scanMove = moves.Single(x => x.EntryId == scan.Document.Id);
        await refuses(() => library.RefileAsync(scan.Document.Id, scanMove.FromPath, "banking/elsewhere.txt", actor), "a move is refused when its destination isn't the one shown");
        var refiled = await library.RefileManyAsync(moves.Where(x => x.Problem == null), actor);
        check(refiled.Moved == moves.Count(x => x.Problem == null) && refiled.Failed.Count == 0, "approved moves all go through");
        check(File.Exists(Path.Combine(scratch, "banking/2026/Cedar Bank/2026-10 Cedar Bank statement 1234.txt")) && !File.Exists(filedPath)
            && File.Exists(Path.Combine(scratch, "banking/2026/Cedar Bank/2026-10 Cedar Bank statement 1234 (2).txt")), "documents move to their new folders, keeping unchanged bytes and never overwriting");
        await using (var db = await factory.CreateDbContextAsync())
            check(await db.Operations.CountAsync(x => x.EntryId == scan.Document.Id && x.Kind == "Move" && x.State == "Completed") == 1, "re-filing goes through the move journal");
        check((await library.RefilePlanAsync(kindId: statementKindNow.Id)).Count == 0, "once moved, nothing is left to re-file");
        await library.SetFollowsPatternsAsync(scan.Document.Id, false, actor);
        statementKindNow = (await library.SortVocabularyAsync()).Kinds.Single(x => x.Name == "Bank statement");
        statementKindNow.FolderPattern = "Statements/{from}"; await library.SaveKindAsync(statementKindNow, actor);
        check((await library.RefilePlanAsync(kindId: statementKindNow.Id)).All(x => x.EntryId != scan.Document.Id), "a document set to stay where it is is never listed to move");
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
