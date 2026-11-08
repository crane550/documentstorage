using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using DocumentStorage.Core;
using DocumentStorage.Core.Formats;
using DocumentStorage.Core.Imaging;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentStorage.AuthenticationChecks;
internal static class PreviewHttpChecks
{
    public static async Task RunAsync(IServiceProvider services,HttpClient client,string scratch,Action<bool,string> check)
    {
        var library=services.GetRequiredService<LibraryService>(); var path=Path.Combine(scratch,"synthetic-preview");Directory.CreateDirectory(path);
        var pdf=System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nSynthetic HTTP preview bytes.\n%%EOF");
        await File.WriteAllBytesAsync(Path.Combine(path,"Synthetic café.pdf"),pdf);
        await File.WriteAllTextAsync(Path.Combine(path,"source.html"),"<script>window.shouldNeverRun=true</script>\nSynthetic plain source.");
        await File.WriteAllTextAsync(Path.Combine(path,"long.txt"),new string('a',DocumentPreview.TextCharacters+20));
        await File.WriteAllTextAsync(Path.Combine(path,"fake.png"),"<html>Not an image</html>");
        await File.WriteAllTextAsync(Path.Combine(path,"unsupported.docx"),"Synthetic unsupported bytes.");
        await File.WriteAllTextAsync(Path.Combine(path,"unicode.txt"),"Synthetic UTF16 café.",System.Text.Encoding.Unicode);
        using(var pages=new MagickImageCollection())
        { pages.Add(new MagickImage(MagickColors.Red,100,80));pages.Add(new MagickImage(MagickColors.Blue,100,80));pages.Write(Path.Combine(path,"Synthetic pages.tiff"),MagickFormat.Tiff); }
        var wordPath=Path.Combine(path,"Synthetic policy.docx");
        using(var zip=ZipFile.Open(wordPath,ZipArchiveMode.Create))
        { using var output=new StreamWriter(zip.CreateEntry("word/document.xml",CompressionLevel.NoCompression).Open(),new UTF8Encoding(false)); output.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Synthetic policy café.</w:t></w:r></w:p></w:body></w:document>"); }
        await File.WriteAllTextAsync(Path.Combine(path,"Synthetic rich text.rtf"),"{\\rtf1 Synthetic rich text preview}");
        using(var zip=ZipFile.Open(Path.Combine(path,"Synthetic slides.pptx"),ZipArchiveMode.Create))
        {
            void Part(string name,string value){using var output=new StreamWriter(zip.CreateEntry(name).Open());output.Write(value);}
            Part("ppt/presentation.xml", """<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId id="256" r:id="s1"/></p:sldIdLst></p:presentation>""");
            Part("ppt/_rels/presentation.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="s1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/></Relationships>""");
            Part("ppt/slides/slide1.xml", """<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Synthetic slide preview</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>""");
        }
        var root=await library.AddRootAsync("Synthetic preview HTTP",path,false);
        var folder=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.ParentId==null);
        var entries=(await library.BrowseAsync(folder.Id)).Items;
        var document=entries.Single(x=>x.Name.EndsWith(".pdf"));
        var raster=entries.Single(x=>x.Name.EndsWith(".tiff"));
        var rasterUrl=$"/documents/{raster.Id}/preview-image?v={DocumentPreview.Revision(raster)}";
        var info=await client.GetFromJsonAsync<RasterInfo>($"/documents/{raster.Id}/preview-image-info?v={DocumentPreview.Revision(raster)}");
        check(info!.Pages==2,"authenticated TIFF metadata reports the available pages");
        using(var response=await client.GetAsync(rasterUrl+"&page=2"))
        {
            var bytes=await response.Content.ReadAsByteArrayAsync();using var rendered=new MagickImage(bytes);
            check(response.StatusCode==HttpStatusCode.OK&&response.Content.Headers.ContentType!.MediaType=="image/png"&&rendered.Width==100&&rendered.Height==80,"authenticated TIFF page conversion serves decoded PNG pixels");
            check(response.Headers.CacheControl?.NoStore==true&&response.Headers.GetValues("X-Content-Type-Options").Contains("nosniff"),"converted raster previews forbid caching and content sniffing");
        }
        check((await client.GetAsync(rasterUrl+"&page=3")).StatusCode==HttpStatusCode.Conflict,"converted preview rejects a page beyond the source document");
        check((await client.GetAsync($"/documents/{raster.Id}/preview?v={DocumentPreview.Revision(raster)}")).StatusCode==HttpStatusCode.Conflict,"raster conversion sources cannot bypass the dedicated preview route");
        check((await client.GetAsync($"/documents/{raster.Id}/preview-image?v=stale&page=1")).StatusCode==HttpStatusCode.Conflict,"converted image previews reject stale revisions");
        string Url(string name,bool text=false) { var item=entries.Single(x=>x.Name==name);return $"/documents/{item.Id}/{(text?"preview-text":"preview")}?v={DocumentPreview.Revision(item)}"; }
        var url=Url(document.Name);
        using(var response=await client.GetAsync(url))
        {
            check(response.StatusCode==HttpStatusCode.OK&&response.Content.Headers.ContentType!.MediaType=="application/pdf"&& (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(pdf),"authenticated preview streams the original PDF bytes with the correct MIME type");
            check(response.Content.Headers.ContentDisposition?.DispositionType=="inline"&&response.Content.Headers.ContentDisposition.FileNameStar!.Contains("café"),"preview disposition is inline with an encoded original filename");
            check(response.Headers.CacheControl?.NoStore==true&&response.Headers.GetValues("X-Content-Type-Options").Contains("nosniff")&&response.Headers.GetValues("Content-Security-Policy").Single().Contains("sandbox"),"raw previews forbid sniffing, caching and active document execution");
        }
        using(var request=new HttpRequestMessage(HttpMethod.Get,url))
        { request.Headers.Range=new RangeHeaderValue(0,4);using var response=await client.SendAsync(request);check(response.StatusCode==HttpStatusCode.PartialContent&&await response.Content.ReadAsStringAsync()=="%PDF-"&&response.Content.Headers.ContentRange?.Length==pdf.Length,"preview supports real HTTP byte ranges for incremental PDF loading"); }
        using(var request=new HttpRequestMessage(HttpMethod.Get,url))
        { request.Headers.Range=new RangeHeaderValue(99999,null);using var response=await client.SendAsync(request);check(response.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable,"invalid preview ranges do not return unrelated bytes"); }
        check((await client.GetAsync($"/documents/{document.Id}/preview?v=stale")).StatusCode==HttpStatusCode.Conflict,"stale preview revisions cannot mix bytes from a changed catalog entry");
        check((await client.GetAsync(Url("fake.png"))).StatusCode==HttpStatusCode.Conflict,"an HTML document renamed to an image cannot be served as inline image content");
        check((await client.GetAsync(Url("unsupported.docx"))).StatusCode==HttpStatusCode.Conflict,"raw Office packages cannot be served as inline app-origin documents");
        var word=entries.Single(x=>x.Name=="Synthetic policy.docx");
        string DerivedUrl()=>$"/documents/{word.Id}/preview-data?v={DocumentPreview.Revision(word)}";
        for(var wait=0;wait<40&&(await library.EntryAsync(word.Id))!.TextStatus!="Indexed";wait++)
        { await library.ProcessNextExtractionAsync(); await Task.Delay(50); }
        var derived=await client.GetFromJsonAsync<DerivedPreview>(DerivedUrl());
        check(derived!.Text.Contains("Synthetic policy café.")&&!derived.Truncated,"authenticated Word previews use the saved Unicode extraction");
        using(var response=await client.GetAsync(DerivedUrl()))
            check(response.Headers.CacheControl?.NoStore==true&&response.Headers.GetValues("X-Content-Type-Options").Contains("nosniff"),"derived preview data forbids caching and content sniffing");
        foreach(var name in new[]{"Synthetic slides.pptx","Synthetic rich text.rtf"})
        {
            var item=entries.Single(x=>x.Name==name);
            for(var wait=0;wait<40&&(await library.EntryAsync(item.Id))!.TextStatus!="Indexed";wait++) { await library.ProcessNextExtractionAsync(); await Task.Delay(50); }
            var previewUrl=$"/documents/{item.Id}/preview-data?v={DocumentPreview.Revision(item)}";
            using var response=await client.GetAsync(previewUrl);
            var data=await response.Content.ReadFromJsonAsync<DerivedPreview>();
            check(response.StatusCode==HttpStatusCode.OK&&data!.Text.Contains("Synthetic")&&(name.EndsWith(".rtf")||data.Artifact?.Sections.Count==1),name+" serves authenticated saved preview data");
            check(response.Headers.CacheControl?.NoStore==true&&response.Headers.GetValues("X-Content-Type-Options").Contains("nosniff"),name+" derived previews forbid caching and sniffing");
            check((await client.GetAsync($"/documents/{item.Id}/preview?v={DocumentPreview.Revision(item)}")).StatusCode==HttpStatusCode.Conflict,name+" cannot serve active original bytes through inline routes");
            check((await client.GetAsync($"/documents/{item.Id}/preview-data?v=stale")).StatusCode==HttpStatusCode.Conflict,name+" saved preview rejects stale revisions");
        }
        var wordBytes=await File.ReadAllBytesAsync(wordPath);var timestamp=File.GetLastWriteTimeUtc(wordPath);
        var tampered=wordBytes.ToArray();var offset=tampered.AsSpan().IndexOf("Synthetic policy"u8);"Tampered! policy"u8.CopyTo(tampered.AsSpan(offset));
        check(tampered.Length==wordBytes.Length,"derived preview tamper fixture preserves the original file size");
        await File.WriteAllBytesAsync(wordPath,tampered);File.SetLastWriteTimeUtc(wordPath,timestamp);
        check((await client.GetAsync(DerivedUrl())).StatusCode==HttpStatusCode.Conflict,"saved Office previews reject changed bytes even when size and timestamp are unchanged");
        await File.WriteAllBytesAsync(wordPath,wordBytes);File.SetLastWriteTimeUtc(wordPath,timestamp);
        var text=await client.GetFromJsonAsync<PreviewText>(Url("source.html",true));
        check(text!.Text.StartsWith("<script>")&&!text.Truncated,"HTML source is returned as JSON text for escaped rendering rather than active HTML");
        text=await client.GetFromJsonAsync<PreviewText>(Url("long.txt",true));
        check(text!.Text.Length==DocumentPreview.TextCharacters&&text.Truncated,"large text previews are explicitly truncated at the display bound");
        text=await client.GetFromJsonAsync<PreviewText>(Url("unicode.txt",true));
        check(text!.Text.Contains("UTF16 café"),"BOM-marked Windows Unicode text previews preserve non-ASCII text");
        var source=Path.Combine(path,document.Name);await File.AppendAllTextAsync(source,"Changed outside the app.");
        check((await client.GetAsync(url)).StatusCode==HttpStatusCode.Conflict,"external file changes are rejected before a rescan can publish a new revision");
        await File.WriteAllBytesAsync(source,pdf);await library.ScanAsync();document=(await library.EntryAsync(document.Id))!;url=$"/documents/{document.Id}/preview?v={DocumentPreview.Revision(document)}";
        // Read sharing allows an app-managed rename while a preview response holds its file handle.
        var opened=await library.OpenPreviewAsync(document.Id,DocumentPreview.Revision(document));
        await using(opened.Stream) { await library.MoveAsync(document.Id,folder.Id,"Renamed.pdf"); check(opened.Stream.Length==pdf.Length,"open preview handles permit app-managed rename while preserving their original byte stream"); }
        check((await client.GetAsync(url)).StatusCode==HttpStatusCode.Conflict,"an old preview URL is invalidated when the document moves");
        document=(await library.EntryAsync(document.Id))!;url=$"/documents/{document.Id}/preview?v={DocumentPreview.Revision(document)}";
        File.Move(Path.Combine(path,StoragePaths.Marker),Path.Combine(path,"marker.offline"));
        try { check((await client.GetAsync(url)).StatusCode==HttpStatusCode.Conflict,"offline storage cannot serve preview bytes from an unverified location"); }
        finally { File.Move(Path.Combine(path,"marker.offline"),Path.Combine(path,StoragePaths.Marker)); }
        await library.TrashEntryAsync(document.Id);
        check((await client.GetAsync(url)).StatusCode==HttpStatusCode.Conflict,"trashed documents cannot be retrieved through their preview URL");
    }
}
