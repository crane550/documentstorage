using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocumentStorage.Core;
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
        var root=await library.AddRootAsync("Synthetic preview HTTP",path,false);
        var folder=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.ParentId==null);
        var entries=(await library.BrowseAsync(folder.Id)).Items;
        var document=entries.Single(x=>x.Name.EndsWith(".pdf"));
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
        check((await client.GetAsync(Url("unsupported.docx"))).StatusCode==HttpStatusCode.Conflict,"unsupported formats use the existing download workflow");
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
