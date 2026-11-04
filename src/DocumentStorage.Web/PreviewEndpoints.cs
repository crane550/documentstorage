using DocumentStorage.Core;
using System.Net.Http.Headers;

namespace DocumentStorage.Web;

public static class PreviewEndpoints
{
    public static void MapDocumentPreviews(this WebApplication app)
    {
        app.MapGet("/documents/{id:guid}/preview", async Task<IResult> (Guid id, string v, HttpContext http, LibraryService library) =>
        {
            try
            {
                var file = await library.OpenPreviewAsync(id,v);
                http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = file.Name }.ToString();
                http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                return Results.File(file.Stream, file.Mime, enableRangeProcessing: true);
            }
            catch (StorageException ex) { return Results.Problem(ex.Message, statusCode: 409); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Problem("The document is unavailable on storage. Reconnect it or rescan.",statusCode:409); }
        }).RequireAuthorization();
        app.MapGet("/documents/{id:guid}/preview-text", async Task<IResult> (Guid id, string v, LibraryService library) =>
        {
            try { return Results.Ok(await library.ReadPreviewTextAsync(id,v)); }
            catch (StorageException ex) { return Results.Problem(ex.Message,statusCode:409); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Problem("The document is unavailable on storage. Reconnect it or rescan.",statusCode:409); }
        }).RequireAuthorization();
    }
}
