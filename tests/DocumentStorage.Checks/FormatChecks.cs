using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Core.Formats;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class FormatChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool, string> check, Func<Func<Task>, string, Task> refuses)
    {
        Directory.CreateDirectory(scratch);
        var formats = DocumentFormatRegistry.CreateDefault();
        var reader = new DocumentTextReader(formats: formats);
        check(formats.Find("UPPER.XLSX")?.Definition.Id == "excel-openxml" && formats.Preview("readme.docx").Kind == "document", "format registry supplies extraction and preview capabilities from the same modules");
        check(formats.Find("picture.svg") is { Definition.ExtractsText: false, Definition.PreviewKind: "text" } && formats.Find("old.xls") is { Definition.ExtractsText: false }, "capabilities distinguish source previews and legacy formats from readable documents");
        var path = Path.Combine(scratch, "synthetic.xlsx"); await File.WriteAllBytesAsync(path, Workbook());
        var result = await reader.ReadAsync(path);
        check(result.Status == "Indexed" && result.FormatId == "excel-openxml" && result.Text.Contains("[Sheet: Accounts]") && result.Text.Contains("A2: Cedar Bank"), "Excel extraction retains sheet names, cell addresses and rich shared strings");
        check(result.Text.Contains("C2: 2026-10-31") && result.Text.Contains("B2: 42.50") && result.Text.Contains("D2: 42.50"), "Excel dates are normalized, number colors do not change cell types, and formulas use saved results");
        check(result.Text.Contains("formula has no saved result: TODAY()") && result.Text.Contains("B1: true") && result.Text.Contains("AA3: Far column"), "missing formula caches, booleans and sparse columns are represented without evaluation");
        var artifact = JsonSerializer.Deserialize<DocumentArtifact>(result.ArtifactJson)!;
        check(artifact.Sections.Count == 2 && artifact.Sections[1].Name == "Notes" && artifact.Sections[1].Rows.Single(x => x.Number == 3).Cells.Single().Column == 27, "the preview artifact keeps worksheet structure and original cell coordinates");
        check(result.Text.Contains("<script>") && artifact.Sections[1].Rows[0].Cells[0].Text.Contains("<script>"), "markup in cell values remains literal document data");
        await File.WriteAllBytesAsync(path, Workbook(extraRows: 250)); result = await reader.ReadAsync(path);
        check(JsonSerializer.Deserialize<DocumentArtifact>(result.ArtifactJson)!.Truncated && result.Text.Contains("A250: Row 250"), "preview row limits are explicit while indexing continues beyond the preview window");
        await File.WriteAllBytesAsync(path, Workbook(externalSheet: true)); result = await reader.ReadAsync(path);
        check(result.Status == "Text extraction failed", "external workbook relationships are not fetched or treated as local worksheets");
        await File.WriteAllBytesAsync(path, Workbook(dtd: true)); result = await reader.ReadAsync(path);
        check(result.Status == "Text extraction failed", "Office XML rejects DTDs and external entity expansion");
        await File.WriteAllTextAsync(path, "Synthetic mislabeled workbook.");
        check((await reader.ReadAsync(path)).Status == "Text extraction failed", "a malformed workbook returns a controlled extraction failure");

        var module = new FixtureModule("1");
        var customReader = new DocumentTextReader(formats: new DocumentFormatRegistry([module]));
        var clock = new Clock(); var library = new LibraryService(factory, customReader, clock, deferExtraction: true);
        while (await library.ProcessNextExtractionAsync()) { } // Retire unrelated old-version fixture jobs.
        var rootPath = Path.Combine(scratch, "queued"); Directory.CreateDirectory(rootPath);
        await File.WriteAllTextAsync(Path.Combine(rootPath, "sample.unit"), "Synthetic module source bytes.");
        var root = await library.AddRootAsync("Synthetic modular extraction", rootPath, false);
        var parent = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var entry = (await library.BrowseAsync(parent.Id)).Items.Single();
        async Task<List<ExtractionJob>> Jobs() { await using var db = await factory.CreateDbContextAsync(); return await db.ExtractionJobs.AsNoTracking().Where(x => x.EntryId == entry.Id).OrderByDescending(x => x.CreatedAt).ToListAsync(); }
        check(entry.TextStatus == "Text queued" && module.Calls == 0, "background discovery persists work without running a format module under the scanner lock");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TextResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        module.Read = _ => { started.TrySetResult(); return finish.Task; };
        var processing = library.ProcessNextExtractionAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await library.CreateFolderAsync(parent.Id, "Still responsive").WaitAsync(TimeSpan.FromSeconds(3));
        check(!await library.ProcessNextExtractionAsync(), "one extraction runs at a time while ordinary folder writes remain responsive");
        finish.SetResult(new("Synthetic extracted content.", "Indexed")); await processing;
        entry = (await library.EntryAsync(entry.Id))!;
        check(entry.TextStatus == "Indexed" && entry.ExtractionVersion == "fixture:1" && entry.ExtractionSourceHash.Length == 64, "extraction results are durably bound to source bytes and the module version");
        await library.ScanAsync(); check(module.Calls == 1 && !await library.ProcessNextExtractionAsync(), "unchanged files reuse saved extraction without asking the module again");

        started = new(TaskCreationOptions.RunContinuationsAsynchronously); finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Advance(1); await library.ReextractDocumentAsync(entry.Id, "format-owner");
        processing = library.ProcessNextExtractionAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await library.MoveAsync(entry.Id, parent.Id, "renamed.unit");
        finish.SetResult(new("An old result must not replace current text.", "Indexed")); await processing;
        check((await library.EntryAsync(entry.Id))!.ExtractedText == "Synthetic extracted content." && (await Jobs()).First(x => x.EntryId == entry.Id).State == "Superseded", "a file moved during extraction rejects the late result");
        clock.Advance(1); await library.ScanAsync();
        var queued = (await Jobs()).First(x => x.EntryId == entry.Id && x.State == "Queued");
        await using (var db = await factory.CreateDbContextAsync()) { var job = await db.ExtractionJobs.FindAsync(queued.Id); job!.State = "Running"; job.Attempts = 1; job.RunId = Guid.NewGuid(); await db.SaveChangesAsync(); }
        module.Read = _ => Task.FromResult(new TextResult("Recovered synthetic extraction.", "Indexed"));
        library = new LibraryService(factory, customReader, clock, deferExtraction: true); await library.RecoverExtractionAsync(); await library.ProcessNextExtractionAsync();
        check((await library.EntryAsync(entry.Id))!.ExtractedText == "Recovered synthetic extraction." && (await Jobs()).Single(x => x.Id == queued.Id).Attempts == 2, "restart recovery resumes durable extraction without resetting its attempt budget");

        var upgraded = new FixtureModule("2");
        library = new LibraryService(factory, new DocumentTextReader(formats: new DocumentFormatRegistry([upgraded])), clock, deferExtraction: true);
        clock.Advance(1); await library.ScanAsync(); await library.ProcessNextExtractionAsync();
        check((await library.EntryAsync(entry.Id))!.ExtractionVersion == "fixture:2" && upgraded.Calls == 1, "a module version upgrade queues unchanged bytes once and stamps the replacement result");
        clock.Advance(1); await library.ReextractDocumentAsync(entry.Id, "format-owner");
        var preserved = (await library.EntryAsync(entry.Id))!.ExtractedText;
        File.Move(Path.Combine(rootPath, StoragePaths.Marker), Path.Combine(rootPath, "marker.offline"));
        try { await library.ProcessNextExtractionAsync(); check((await library.EntryAsync(entry.Id))!.ExtractedText == preserved && (await Jobs()).First(x => x.EntryId == entry.Id).State == "Retry", "offline storage retains saved content and leaves extraction waiting"); }
        finally { File.Move(Path.Combine(rootPath, "marker.offline"), Path.Combine(rootPath, StoragePaths.Marker)); }
        clock.Advance(31); await library.ProcessNextExtractionAsync();
        check((await library.EntryAsync(entry.Id))!.TextStatus == "Indexed", "waiting extraction completes after storage returns");
        clock.Advance(1); await library.ReextractDocumentAsync(entry.Id, "format-owner");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.ExtractionJobs.SingleAsync(x => x.EntryId == entry.Id && x.State == "Queued");
            job.State = "Running"; job.Attempts = 3; job.RunId = Guid.NewGuid();
            var current = await db.Entries.FindAsync(entry.Id); current!.TextStatus = "Extracting"; current.ExtractionVersion = "";
            await db.SaveChangesAsync();
        }
        await library.RecoverExtractionAsync(); await library.ScanAsync();
        check((await Jobs()).First().State == "Failed" && !await library.ProcessNextExtractionAsync(), "a restart at the retry limit does not start a new extraction budget on rescan");
        clock.Advance(1); await library.ReextractDocumentAsync(entry.Id, "format-owner");
        await library.MoveAsync(entry.Id, parent.Id, "moved-before-extraction.unit");
        await library.ProcessNextExtractionAsync();
        check((await library.EntryAsync(entry.Id))!.TextStatus == "Pending", "moving a queued document leaves it eligible for extraction instead of stuck waiting");
        await library.ScanAsync(); await library.ProcessNextExtractionAsync();
        check((await library.EntryAsync(entry.Id))!.TextStatus == "Indexed", "a document moved before its extraction claim is indexed at its current path");
        upgraded.Read = _ => Task.FromResult(new TextResult("", "Text extraction timed out"));
        clock.Advance(1); await library.ReextractDocumentAsync(entry.Id, "format-owner"); await library.ProcessNextExtractionAsync();
        check((await Jobs()).First().State == "Failed" && (await Jobs()).First().Error == "Text extraction timed out", "a module timeout is reported as a failed extraction rather than a completed job");
    }
    internal static byte[] Workbook(int extraRows = 0, bool externalSheet = false, bool dtd = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Part(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
            Part("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>""");
            Part("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="main" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Part("xl/workbook.xml", (dtd ? "<!DOCTYPE workbook [<!ENTITY unsafe SYSTEM 'file:///synthetic-unavailable'>]>" : "") + """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Accounts" sheetId="1" r:id="s1"/><sheet name="Notes" sheetId="2" r:id="s2"/></sheets></workbook>""");
            var mode = externalSheet ? " TargetMode=\"External\"" : "";
            Part("xl/_rels/workbook.xml.rels", $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="s1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"{mode}/><Relationship Id="s2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"{mode}/></Relationships>""");
            Part("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Bank</t></si><si><t>Balance</t></si><si><r><t>Cedar </t></r><r><t>Bank</t></r></si></sst>""");
            Part("xl/styles.xml", """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="[Red]0.00"/></numFmts><cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="14"/><xf numFmtId="164"/></cellXfs></styleSheet>""");
            var serial = (new DateTime(2026, 10, 31) - new DateTime(1899, 12, 30)).TotalDays.ToString(CultureInfo.InvariantCulture);
            var rows = string.Concat(Enumerable.Range(3, extraRows).Select(i => $"<row r=\"{i}\"><c r=\"A{i}\" t=\"inlineStr\"><is><t>Row {i}</t></is></c></row>"));
            Part("xl/worksheets/sheet1.xml", $"""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row><row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2" s="2"><v>42.50</v></c><c r="C2" s="1"><v>{serial}</v></c><c r="D2"><f>SUM(B2)</f><v>42.50</v></c><c r="E2"><f>TODAY()</f></c></row>{rows}</sheetData></worksheet>""");
            Part("xl/worksheets/sheet2.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>&lt;script&gt;window.cellMustNotRun=true&lt;/script&gt;</t></is></c><c r="B1" t="b"><v>1</v></c></row><row r="3"><c r="AA3" t="inlineStr"><is><t>Far column</t></is></c></row></sheetData></worksheet>""");
        }
        return output.ToArray();
    }
    private sealed class FixtureModule(string version) : IDocumentFormatModule
    {
        public IReadOnlyList<DocumentFormat> Formats { get; } = [new("fixture", version, "Synthetic format", [".unit"], true, "document", "application/octet-stream", 10000)];
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<TextResult>> Read { get; set; } = _ => Task.FromResult(new TextResult("Synthetic versioned text.", "Indexed"));
        public Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default) { Calls++; return Read(cancellationToken); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
