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
        app.MapGet("/documents/{id:guid}/preview-image-info", async Task<IResult> (Guid id, string v, LibraryService library, CancellationToken token) =>
        {
            try { return Results.Ok(await library.DescribeImagePreviewAsync(id, v, token)); }
            catch (StorageException ex) { return Results.Problem(ex.Message, statusCode: 409); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Problem("The image is unavailable on storage. Reconnect it or rescan.", statusCode: 409); }
        }).RequireAuthorization();
        app.MapGet("/documents/{id:guid}/preview-image", async Task<IResult> (Guid id, string v, int page, LibraryService library, CancellationToken token) =>
        {
            try { return Results.File(await library.RenderImagePreviewAsync(id, v, page, token), "image/png"); }
            catch (StorageException ex) { return Results.Problem(ex.Message, statusCode: 409); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Problem("The image is unavailable on storage. Reconnect it or rescan.", statusCode: 409); }
        }).RequireAuthorization();
        app.MapGet("/documents/{id:guid}/preview-text", async Task<IResult> (Guid id, string v, LibraryService library) =>
        {
            try { return Results.Ok(await library.ReadPreviewTextAsync(id,v)); }
            catch (StorageException ex) { return Results.Problem(ex.Message,statusCode:409); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Problem("The document is unavailable on storage. Reconnect it or rescan.",statusCode:409); }
        }).RequireAuthorization();
        app.MapGet("/documents/{id:guid}/preview-data", async Task<IResult> (Guid id, string v, LibraryService library) =>
        {
            try { return Results.Ok(await library.ReadDerivedPreviewAsync(id, v)); }
            catch (StorageException ex) when (ex.Message.StartsWith("preview_preparing:", StringComparison.Ordinal)) { return Results.Problem("Preparing text preview. Please wait.", statusCode: 425); }
            catch (StorageException ex) { return Results.Problem(ex.Message, statusCode: 409); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Problem("The document is unavailable on storage. Reconnect it or rescan.", statusCode: 409); }
        }).RequireAuthorization();
    }
}
