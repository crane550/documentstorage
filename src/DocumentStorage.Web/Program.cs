using System.Net;
using DocumentStorage.Core;
using DocumentStorage.Data;
using DocumentStorage.Web;
using DocumentStorage.Web.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var instance = Environment.GetEnvironmentVariable("DOCUMENTSTORAGE_INSTANCE");
if (!string.IsNullOrWhiteSpace(instance)) builder.Configuration.AddJsonFile(instance, optional: false, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// The first milestone is local-only until authenticated server access is implemented.
var urls = builder.Configuration["Urls"] ?? "http://127.0.0.1:5240";
foreach (var value in urls.Split(';'))
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        !(uri.Host == "localhost" || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))))
        throw new InvalidOperationException("This development milestone must bind to loopback. Authenticated LAN/public hosting is not implemented yet.");
builder.WebHost.UseUrls(urls);
var connection = builder.Configuration.GetConnectionString("Catalog") ?? throw new InvalidOperationException("Set ConnectionStrings__Catalog to this instance's PostgreSQL database.");
builder.Services.AddDbContextFactory<CatalogDb>(options => options.UseNpgsql(connection));
builder.Services.AddSingleton(new DocumentTextReader(builder.Configuration["Documents:PdfTextCommand"] ?? "pdftotext"));
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddHostedService<LibraryScanner>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment());

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<CatalogDb>>().CreateDbContextAsync())
    await db.Database.MigrateAsync();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.Use(async (context, next) =>
{
    if (!context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !(IPAddress.TryParse(context.Request.Host.Host, out var ip) && IPAddress.IsLoopback(ip)))
    { context.Response.StatusCode = 400; return; }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
app.UseAntiforgery();
app.MapGet("/documents/{id:guid}/download", async Task<IResult> (Guid id, LibraryService library) =>
{
    try
    {
        var file = await library.OpenDocumentAsync(id);
        return Results.File(file.Stream, "application/octet-stream", file.Name, enableRangeProcessing: true);
    }
    catch (StorageException ex) { return Results.Problem(ex.Message, statusCode: 409); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    { return Results.Problem("The file is unavailable on storage. Reconnect it or rescan.", statusCode: 409); }
});
app.MapGet("/health", async (IDbContextFactory<CatalogDb> factory) =>
{
    await using var db = await factory.CreateDbContextAsync();
    return await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
});
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
