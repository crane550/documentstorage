using System.Net;
using DocumentStorage.Core;
using DocumentStorage.Data;
using DocumentStorage.Web;
using DocumentStorage.Web.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
var instance = Environment.GetEnvironmentVariable("DOCUMENTSTORAGE_INSTANCE");
builder.Configuration.AddJsonFile(instance ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "instance.json")), optional: instance == null, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Host.UseWindowsService(options => options.ServiceName = builder.Configuration["Hosting:ServiceName"] ?? "DocumentStorage");
if (OperatingSystem.IsWindows()) builder.Logging.AddEventLog(options =>
{
    if (OperatingSystem.IsWindows()) options.SourceName = "DocumentStorage";
});
var hosting = PublicHosting.Read(builder.Configuration, builder.Environment.IsDevelopment());
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]" + (hosting.Enabled ? ";" + hosting.PublicOrigin!.Host : "");
var keyDirectory = builder.Configuration["Hosting:DataProtectionDirectory"];
if (!string.IsNullOrWhiteSpace(keyDirectory))
{
    var protection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
    if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
}
if (hosting.Enabled)
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    });

// cloudflared is the only ingress. Never open the document origin on the LAN.
var urls = builder.Configuration["Urls"] ?? "http://127.0.0.1:5240";
foreach (var value in urls.Split(';'))
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        !(uri.Host == "localhost" || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))))
        throw new InvalidOperationException("The document origin must bind to loopback. Use the authenticated Cloudflare tunnel for remote access.");
builder.WebHost.UseUrls(urls);
var connection = builder.Configuration.GetConnectionString("Catalog") ?? throw new InvalidOperationException("Set ConnectionStrings__Catalog to this instance's PostgreSQL database.");
var password = builder.Configuration["DOCUMENTSTORAGE_DB_PASSWORD"];
if (!string.IsNullOrEmpty(password)) connection = new NpgsqlConnectionStringBuilder(connection) { Password = password }.ConnectionString;
var database = new NpgsqlConnectionStringBuilder(connection).Database ?? throw new InvalidOperationException("The catalog connection must name a database.");
builder.Services.AddDataProtection().SetApplicationName("DocumentStorage:" + database);
builder.Services.AddLibraryIdentity(hosting, database);
builder.Services.AddDbContextFactory<CatalogDb>(options => options.UseNpgsql(connection));
builder.Services.AddSingleton(new DocumentTextReader(builder.Configuration["Documents:PdfTextCommand"] ?? "pdftotext"));
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddHostedService<LibraryScanner>();
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<IdentificationWorker>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment());

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<CatalogDb>>().CreateDbContextAsync())
    await db.Database.MigrateAsync();
await using (var scope = app.Services.CreateAsyncScope())
    await IdentitySetup.SeedAdministratorAsync(scope.ServiceProvider, builder.Configuration);
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
if (hosting.Enabled) app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    if (!hosting.AllowsHost(context.Request.Host.Host))
    { context.Response.StatusCode = 400; return; }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapAccountEndpoints();
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
    return await db.Database.CanConnectAsync() ? Results.Ok(new
    {
        status = "ready",
        revision = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development"
    }) : Results.StatusCode(503);
}).AllowAnonymous();
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
