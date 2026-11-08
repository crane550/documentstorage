using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Core.Formats;
using DocumentStorage.Core.Office;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class AdditionalOfficeChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory,string scratch,Action<bool,string> check,Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);await WriteFixturesAsync(scratch);
        var registry=DocumentFormatRegistry.CreateDefault();var reader=new DocumentTextReader(formats:registry);
        var names=new[]{"Synthetic policy.doc","Synthetic workbook.xls","Synthetic training.pptx","Synthetic rich text.rtf","Synthetic policy.odt","Synthetic workbook.ods"};
        foreach(var name in names)
        {
            var path=Path.Combine(scratch,name);var before=SHA256.HashData(await File.ReadAllBytesAsync(path));var result=await reader.ReadAsync(path);
            check(result.Status=="Indexed"&&result.Text.Contains("Synthetic"),name+" extracts real document content for search and AI");
            var after=SHA256.HashData(await File.ReadAllBytesAsync(path));check(before.SequenceEqual(after),name+" extraction preserves original bytes");
        }
        var rtf=await reader.ReadAsync(Path.Combine(scratch,"Synthetic rich text.rtf"));
        check(rtf.Text.Contains("Café")&&rtf.Text.Contains("你好")&&rtf.Text.Contains("😀")&&rtf.Text.Contains("€"),"RTF decodes ANSI escapes, Unicode fallback and surrogate pairs");
        check(rtf.Text.Contains("Visible field")&&!rtf.Text.Contains("SECRET")&&!rtf.Text.Contains("HIDDEN")&&!rtf.Text.Contains("fetch.example"),"RTF omits hidden text, field instructions, ignorable destinations and embedded objects");
        var ppt=await reader.ReadAsync(Path.Combine(scratch,"Synthetic training.pptx"));var slides=JsonSerializer.Deserialize<DocumentArtifact>(ppt.ArtifactJson)!;
        check(slides.Sections.Count==2&&slides.Sections[0].Name=="Synthetic training"&&ppt.Text.IndexOf("FIRST-SLIDE",StringComparison.Ordinal)<ppt.Text.IndexOf("SECOND-SLIDE",StringComparison.Ordinal),"PowerPoint follows presentation relationships rather than slide filenames");
        check(ppt.Text.Contains("Speaker notes")&&ppt.Text.Contains("NOTE-TOKEN")&&!ppt.Text.Contains("FOOTER-SECRET"),"PowerPoint indexes speaker notes while excluding slide-number/footer placeholders");
        var ods=await reader.ReadAsync(Path.Combine(scratch,"Synthetic workbook.ods"));var sheets=JsonSerializer.Deserialize<DocumentArtifact>(ods.ArtifactJson)!;
        check(sheets.Sections.Count==2&&ods.Text.Contains("C3: 42.5")&&ods.Text.Contains("B4: true")&&ods.Text.Contains("B5: 2026-11-08"),"OpenDocument preserves repeated empty coordinates and typed saved values across sheets");
        check(ods.Text.Contains("formula has no saved result")&&!ods.Text.Contains("EVIL-QUERY-RESULT"),"OpenDocument reports missing formula caches without executing formulas or external links");
        var odt=await reader.ReadAsync(Path.Combine(scratch,"Synthetic policy.odt"));
        check(odt.Text.Contains("Café  training\nsecond line")&&!odt.Text.Contains("DELETED-SECRET"),"OpenDocument text retains spaces and breaks and excludes deleted tracked text");
        var malformed=Path.Combine(scratch,"malformed.rtf");await File.WriteAllTextAsync(malformed,"{\\rtf1 unbalanced");
        check((await reader.ReadAsync(malformed)).Status=="Text extraction failed","unbalanced RTF fails without returning a misleading partial index");
        await File.WriteAllTextAsync(malformed,"{\\rtf1"+new string('{',257)+"too deep"+new string('}',258));
        check((await reader.ReadAsync(malformed)).Status=="Text extraction failed","RTF nesting is bounded");
        await File.WriteAllTextAsync(malformed,"{\\rtf1 \\bin9999999 short}");
        check((await reader.ReadAsync(malformed)).Status=="Text extraction failed","RTF binary payload lengths cannot escape the bounded input");
        await File.WriteAllTextAsync(malformed,"{\\rtf1 \\uc1 \\u20320\\'3f\\u22909?}");
        check((await reader.ReadAsync(malformed)).Text.Contains("你好"),"RTF Unicode fallback handles both hex and plain fallback bytes");
        await File.WriteAllTextAsync(malformed,"{\\rtf1 "+new string('a',DocumentTextReader.MaxCharacters+10)+"}");
        var limited=await reader.ReadAsync(malformed);check(limited.Truncated&&limited.Text.Length==DocumentTextReader.MaxCharacters,"RTF extraction reports its character limit");
        await File.WriteAllTextAsync(malformed,"{\\rtf1 Before{\\pict\\bin4 {}}x}After}");
        check((await reader.ReadAsync(malformed)).Text=="BeforeAfter","RTF binary braces stay inside the skipped picture payload");
        await File.WriteAllTextAsync(malformed,"{\\rtf1 \\u-10179?}");
        check((await reader.ReadAsync(malformed)).Text=="\uFFFD","unpaired RTF Unicode escapes become safe replacement characters for UTF-8 storage");
        var ansiPrefix=Encoding.ASCII.GetBytes("{\\rtf1\\ansi\\ansicpg932 "+new string('a',4095));
        await File.WriteAllBytesAsync(malformed,[..ansiPrefix,..Encoding.GetEncoding(932).GetBytes("ひ"),(byte)'}']);
        check((await reader.ReadAsync(malformed)).Text.EndsWith("ひ",StringComparison.Ordinal),"RTF multibyte ANSI text is not split at an internal buffer boundary");
        var hostile=Path.Combine(scratch,"hostile.ods");
        WriteOdf(hostile,true,"""<table:table table:name="Bounded"><table:table-row table:number-rows-repeated="2147483647"><table:table-cell table:number-columns-repeated="2147483647"/></table:table-row><table:table-row><table:table-cell office:value-type="string"><text:p>Beyond row limit</text:p></table:table-cell></table:table-row></table:table>""");
        var watch=Stopwatch.StartNew();limited=await reader.ReadAsync(hostile);
        check(limited.Status=="Indexed"&&limited.Truncated&&watch.Elapsed<TimeSpan.FromSeconds(5),"enormous empty ODS repetitions advance coordinates without materializing billions of cells");
        WriteOdf(hostile,true,"""<table:table table:name="Bounded"><table:table-row table:number-rows-repeated="2000000"><table:table-cell office:value-type="string"><text:p>Repeated</text:p></table:table-cell></table:table-row></table:table>""");
        limited=await reader.ReadAsync(hostile);check(limited.Status=="Indexed"&&limited.Truncated&&limited.Text.Length<=DocumentTextReader.MaxCharacters,"nonempty repeated ODS cells stop at extraction and preview limits");
        WriteOdf(hostile,true,"<table:table table:name=\"DTD\"/>",dtd:true);
        check((await reader.ReadAsync(hostile)).Status=="Text extraction failed","OpenDocument XML refuses DTD/entity expansion");
        WriteOdf(hostile,true,"",encrypted:true);
        check((await reader.ReadAsync(hostile)).Status=="Password-protected document","encrypted OpenDocument content is distinguished from an empty document");
        File.Copy(Path.Combine(scratch,"Synthetic policy.odt"),hostile,true);
        check((await reader.ReadAsync(hostile)).Status=="Office contents do not match extension","OpenDocument checks the package media type against its extension");
        var badPpt=Path.Combine(scratch,"hostile.pptx");WritePresentation(badPpt,external:true);
        check((await reader.ReadAsync(badPpt)).Status=="Text extraction failed","external PowerPoint slide relationships are not followed");
        WritePresentation(badPpt,escape:true);
        check((await reader.ReadAsync(badPpt)).Status=="Text extraction failed","PowerPoint relationships cannot escape the package");
        WritePresentation(badPpt,longText:true);
        limited=await reader.ReadAsync(badPpt);var boundedSlides=JsonSerializer.Deserialize<DocumentArtifact>(limited.ArtifactJson)!;
        check(boundedSlides.Truncated&&boundedSlides.Sections[0].Rows[0].Cells[0].Text.Length==150000&&limited.Text.Length>150000,"large slide text retains a bounded preview while full indexing continues");
        var missing=new OfficeConverter(Path.Combine(scratch,"missing-office-converter"));
        check(!(await missing.CheckAsync()).Available&&(await missing.ReadLegacyAsync(Path.Combine(scratch,"Synthetic policy.doc"))).Status=="Office converter unavailable","missing LibreOffice has a useful runtime check and extraction status");
        await File.WriteAllTextAsync(Path.Combine(scratch,"fake.doc"),"Not an OLE document");
        check((await reader.ReadAsync(Path.Combine(scratch,"fake.doc"))).Status=="Office contents do not match extension","legacy conversion refuses a misleading file extension before starting a tool");
        using(var cancelled=new CancellationTokenSource())
        {cancelled.Cancel();try{await reader.ReadAsync(Path.Combine(scratch,"Synthetic policy.doc"),cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){check(true,"legacy conversion observes cancellation");}}

        var library=new LibraryService(factory,reader,deferExtraction:true);var storage=Path.Combine(scratch,"storage");Directory.CreateDirectory(storage);
        foreach(var name in names)File.Copy(Path.Combine(scratch,name),Path.Combine(storage,name));
        var root=await library.AddRootAsync("Synthetic additional Office formats",storage,false);var folder=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.ParentId==null);
        while(await library.ProcessNextExtractionAsync()){}
        var entries=(await library.BrowseAsync(folder.Id)).Items;var entryIds=entries.Select(x=>x.Id).ToArray();
        foreach(var entry in entries)
        {
            check(entry.TextStatus=="Indexed"&&entry.ExtractionSourceHash.Length==64&&entry.ExtractionVersion==registry.VersionFor(entry.Name),entry.Name+" saves its index with the original fingerprint and module version");
            var preview=await library.ReadDerivedPreviewAsync(entry.Id,DocumentPreview.Revision(entry));
            check(preview.Text.Contains("Synthetic")&&(registry.Preview(entry.Name).Kind!="presentation"||preview.Artifact?.Sections.Count==2),entry.Name+" serves a validated saved preview");
        }
        var found=await library.BrowseAsync(null,"NOTE-TOKEN");check(found.Items.Any(x=>x.RootId==root&&x.Name.EndsWith(".pptx")),"PowerPoint notes feed the real PostgreSQL-backed search");
        async Task<int> JobCount(){await using var db=await factory.CreateDbContextAsync();return await db.ExtractionJobs.CountAsync(x=>entryIds.Contains(x.EntryId));}
        var count=await JobCount();await library.ScanAsync();
        check(await JobCount()==count,"rescan reuses all six saved format results instead of extracting again");
        var legacy=entries.Single(x=>x.Name.EndsWith(".doc"));await library.ReextractDocumentAsync(legacy.Id,"office-check-owner");while(await library.ProcessNextExtractionAsync()){}
        check((await library.EntryAsync(legacy.Id))!.TextStatus=="Indexed"&&await JobCount()==count+1,"explicit re-extraction refreshes legacy Office content once");
        await refuses(async()=>{var opened=await library.OpenPreviewAsync(legacy.Id,DocumentPreview.Revision(legacy));await opened.Stream.DisposeAsync();},"legacy originals cannot bypass derived previews through raw inline routes");
        var clock=new Clock();library=new LibraryService(factory,new DocumentTextReader(formats:DocumentFormatRegistry.CreateDefault(office:missing)),clock,deferExtraction:true);
        await library.ReextractDocumentAsync(legacy.Id,"office-check-owner");while(await library.ProcessNextExtractionAsync()){}
        await using(var db=await factory.CreateDbContextAsync())
        {var job=await db.ExtractionJobs.Where(x=>x.EntryId==legacy.Id).OrderByDescending(x=>x.CreatedAt).FirstAsync();check(job.State=="Retry"&&job.Attempts==0&&job.Error=="Office converter unavailable","missing Office converter waits without consuming the retry budget");}
        library=new LibraryService(factory,reader,clock,deferExtraction:true);clock.Advance(61);while(await library.ProcessNextExtractionAsync()){}
        check((await library.EntryAsync(legacy.Id))!.TextStatus=="Indexed","waiting legacy extraction succeeds after the converter returns");
    }
    public static async Task WriteFixturesAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        WriteOdf(Path.Combine(directory,"Synthetic policy.odt"),false,"""<text:h text:outline-level="1">Synthetic training policy</text:h><text:p>Café<text:s text:c="2"/>training<text:line-break/>second line</text:p><text:p>Emergency exits POLICY-TOKEN &lt;script&gt;window.odfMustNotRun=true&lt;/script&gt;</text:p><text:tracked-changes><text:changed-region><text:deletion><text:p>DELETED-SECRET</text:p></text:deletion></text:changed-region></text:tracked-changes>""");
        WriteOdf(Path.Combine(directory,"Synthetic workbook.ods"),true,"""<table:table table:name="Accounts"><table:table-row><table:table-cell office:value-type="string"><text:p>Synthetic bank</text:p></table:table-cell></table:table-row><table:table-row table:number-rows-repeated="1"><table:table-cell table:number-columns-repeated="2"/></table:table-row><table:table-row><table:table-cell table:number-columns-repeated="2"/><table:table-cell office:value-type="float" office:value="42.5" table:formula="of:=SUM([.A1])"/></table:table-row><table:table-row><table:table-cell/><table:table-cell office:value-type="boolean" office:boolean-value="true"/></table:table-row><table:table-row><table:table-cell/><table:table-cell office:value-type="date" office:date-value="2026-11-08"/></table:table-row><table:table-row><table:table-cell table:formula="of:=WEBSERVICE(&quot;https://fetch.example&quot;)"/></table:table-row></table:table><table:table table:name="Notes"><table:table-row><table:table-cell office:value-type="string"><text:p>&lt;script&gt;window.odsMustNotRun=true&lt;/script&gt;</text:p></table:table-cell></table:table-row></table:table>""");
        WritePresentation(Path.Combine(directory,"Synthetic training.pptx"));
        await File.WriteAllTextAsync(Path.Combine(directory,"Synthetic rich text.rtf"),"""{\rtf1\ansi\ansicpg1252\uc1 Synthetic rich text Caf\'e9 \u20320?\u22909? \u-10179?\u-8704? \'80\par Emergency exits RTF-TOKEN <script>window.rtfMustNotRun=true</script>{\*\unknown SECRET}{\pict SECRET}{\object\objdata SECRET}{\v HIDDEN}{\field{\*\fldinst INCLUDETEXT https://fetch.example}{\fldrslt Visible field}}} """,new UTF8Encoding(false));
        await File.WriteAllBytesAsync(Path.Combine(directory,"Synthetic native.xlsx"),FormatChecks.Workbook());
        await ConvertFixture(directory,"Synthetic policy.odt","doc:MS Word 97");
        await ConvertFixture(directory,"Synthetic workbook.ods","xls:MS Excel 97");
    }
    private static async Task ConvertFixture(string directory,string name,string format)
    {
        var profile=Path.Combine(Path.GetFullPath(directory),"fixture-profile-"+Guid.NewGuid().ToString("N"));
        try
        {
            var start=new ProcessStartInfo(OfficeConverter.DefaultCommand()){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"-env:UserInstallation="+new Uri(profile).AbsoluteUri,"--headless","--convert-to",format,"--outdir",Path.GetFullPath(directory),Path.GetFullPath(Path.Combine(directory,name))})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try{await process.WaitForExitAsync(timeout.Token);await Task.WhenAll(output,error);}catch{if(!process.HasExited)process.Kill(true);throw;}
            if(process.ExitCode!=0||!File.Exists(Path.Combine(directory,Path.ChangeExtension(name,format.StartsWith("doc")?"doc":"xls"))))throw new Exception("Synthetic legacy fixture conversion failed.");
        }
        finally{if(Directory.Exists(profile))Directory.Delete(profile,true);}
    }
    private static void WriteOdf(string path,bool spreadsheet,string body,bool dtd=false,bool encrypted=false)
    {
        if(File.Exists(path))File.Delete(path);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        Part(zip,"mimetype","application/vnd.oasis.opendocument."+(spreadsheet?"spreadsheet":"text"));
        Part(zip,"META-INF/manifest.xml","""<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument."""+(spreadsheet?"spreadsheet":"text")+"\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\">"+(encrypted?"<manifest:encryption-data/>":"")+"</manifest:file-entry></manifest:manifest>");
        Part(zip,"content.xml",(dtd?"<!DOCTYPE office:document-content [<!ENTITY unsafe SYSTEM 'file:///synthetic-unavailable'>]>":"")+"""<office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" office:version="1.2"><office:body>"""+(spreadsheet?"<office:spreadsheet>":"<office:text>")+body+(spreadsheet?"</office:spreadsheet>":"</office:text>")+"</office:body></office:document-content>");
    }
    private static void WritePresentation(string path,bool external=false,bool escape=false,bool longText=false)
    {
        if(File.Exists(path))File.Delete(path);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        Part(zip,"ppt/presentation.xml","""<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId id="256" r:id="first"/><p:sldId id="257" r:id="second"/></p:sldIdLst></p:presentation>""");
        Part(zip,"ppt/_rels/presentation.xml.rels","""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="first" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="""+"\""+(escape?"../../outside.xml":"slides/slide9.xml")+"\""+(external?" TargetMode=\"External\"":"")+"""/><Relationship Id="second" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide2.xml"/></Relationships>""");
        Part(zip,"ppt/slides/slide9.xml",Slide("Synthetic training",longText?new string('a',200000):"FIRST-SLIDE &lt;script&gt;window.pptMustNotRun=true&lt;/script&gt;"));
        Part(zip,"ppt/slides/slide2.xml",Slide("Emergency exits","SECOND-SLIDE emergency procedure"));
        Part(zip,"ppt/slides/_rels/slide9.xml.rels","""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="notes" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide" Target="../notesSlides/notesSlide1.xml"/><Relationship Id="untrusted" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://fetch.example" TargetMode="External"/></Relationships>""");
        Part(zip,"ppt/notesSlides/notesSlide1.xml","""<p:notes xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type="body"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>NOTE-TOKEN Synthetic speaker guidance</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:nvSpPr><p:nvPr><p:ph type="ftr"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>FOOTER-SECRET</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>""");
    }
    private static string Slide(string title,string body)=>"""<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>"""+title+"""</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:txBody><a:p><a:r><a:t>"""+body+"</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
    private static void Part(ZipArchive zip,string name,string content){using var output=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));output.Write(content);}
    private sealed class Clock:TimeProvider{private DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;public void Advance(int seconds)=>now=now.AddSeconds(seconds);}
}
