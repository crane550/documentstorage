using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DocumentStorage.Web.Mcp;

public sealed record McpAddress(Uri Issuer)
{
    public string Resource => new Uri(Issuer, "mcp").AbsoluteUri;
    public string Metadata => new Uri(Issuer, ".well-known/oauth-protected-resource/mcp").AbsoluteUri;
}
public sealed class McpActiveRequirement : IAuthorizationRequirement;
public sealed class McpActiveHandler(IDbContextFactory<CatalogDb> factory, UserManager<LibraryUser> users) : AuthorizationHandler<McpActiveRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, McpActiveRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || !Guid.TryParse(context.User.FindFirstValue("ds_connection"), out var id)) return;
        await using var db = await factory.CreateDbContextAsync();
        var connection = await db.AssistantConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        var user = await users.FindByIdAsync(context.User.GetClaim(Claims.Subject) ?? "");
        if (connection == null || connection.RevokedAt != null || user == null || connection.OwnerId != user.Id ||
            context.User.GetClaim("ds_stamp") != await users.GetSecurityStampAsync(user) || await users.IsLockedOutAsync(user) || !await users.IsInRoleAsync(user, IdentitySetup.Administrator)) return;
        var allowed = JsonSerializer.Deserialize<string[]>(connection.ScopesJson)!;
        if (context.User.GetScopes().Any(x => x != Scopes.OfflineAccess && !allowed.Contains(x))) return;
        context.Succeed(requirement);
    }
}
public sealed class McpSigningKeys(IDataProtectionProvider provider, IConfiguration configuration, IWebHostEnvironment environment) : IConfigureOptions<OpenIddictServerOptions>
{
    private static readonly object gate = new();
    public void Configure(OpenIddictServerOptions options)
    {
        var directory = Path.Combine(configuration["Hosting:DataProtectionDirectory"] ?? Path.Combine(environment.ContentRootPath, ".local", "keys"), "oauth");
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RsaSecurityKey Key(string name)
            {
                var path = Path.Combine(directory, name + ".key");
                var protector = provider.CreateProtector("DocumentStorage.OAuth.Keys.v1", name);
                var rsa = RSA.Create();
                if (File.Exists(path)) rsa.ImportPkcs8PrivateKey(protector.Unprotect(File.ReadAllBytes(path)), out _);
                else
                {
                    rsa.KeySize = 3072;
                    var bytes = protector.Protect(rsa.ExportPkcs8PrivateKey());
                    var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using var output = new FileStream(path, fileOptions);
                    output.Write(bytes); output.Flush(flushToDisk: true);
                }
                return new(rsa) { KeyId = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..24] };
            }
            options.SigningCredentials.Add(new(Key("signing"), SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new(Key("encryption"), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        }
    }
}
public sealed record RegisteredAssistant(AssistantConnection Connection, string? Secret);
public sealed class AssistantConnections(IOpenIddictApplicationManager applications, IDbContextFactory<CatalogDb> factory, LibraryService library, CatalogDb context, McpAddress address)
{
    public async Task<List<AssistantConnection>> ListAsync(string actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.AssistantConnections.AsNoTracking().Where(x => x.OwnerId == actor).OrderByDescending(x => x.CreatedAt).ToListAsync();
    }
    public async Task<RegisteredAssistant> CreateAsync(string name, string[] redirects, string[] scopes, bool publicClient, string actor)
    {
        name = name.Trim(); scopes = scopes.Distinct().Order().ToArray(); redirects = redirects.Distinct().ToArray();
        if (name.Length is 0 or > 80 || actor.Length is 0 or > 256 || redirects.Length is 0 or > 5 || scopes.Length > LibraryService.AssistantScopes.Length || !scopes.Contains("catalog.read") || scopes.Any(x => !LibraryService.AssistantScopes.Contains(x)))
            throw new StorageException("Choose a name, 1–5 callback URLs and allowed permissions, including catalog.read.");
        if (scopes.Contains("filing.execute") && !scopes.Contains("documents.read")) throw new StorageException("Filing permission also requires document-read permission for reviewing the preview.");
        if(scopes.Contains("sorting.manage")&&!scopes.Contains("documents.read"))throw new StorageException("Direct corrections and training also require document-read permission.");
        var uris = new List<Uri>();
        foreach (var redirect in redirects)
        {
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || redirect.Length > 1000 ||
                !(uri.Scheme == "https" || (publicClient && uri.Scheme == "http" && uri.IsLoopback)))
                throw new StorageException("Use complete HTTPS callback URLs without credentials, query or fragment. Public desktop clients also support loopback HTTP.");
            uris.Add(uri);
        }
        var db = context;
        if (await db.AssistantConnections.CountAsync(x => x.OwnerId == actor && x.RevokedAt == null) >= 20) throw new StorageException("Revoke an unused connection before adding another. Up to 20 active connections are supported.");
        var connection = new AssistantConnection { Name = name, OwnerId = actor, ClientId = "ds_" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24)), ScopesJson = JsonSerializer.Serialize(scopes), CreatedAt = DateTimeOffset.UtcNow };
        var secret = publicClient ? null : Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        var descriptor = new OpenIddictApplicationDescriptor { ClientId = connection.ClientId, ClientSecret = secret, DisplayName = name,
            ApplicationType = publicClient ? ApplicationTypes.Native : ApplicationTypes.Web, ClientType = publicClient ? ClientTypes.Public : ClientTypes.Confidential, ConsentType = ConsentTypes.Explicit };
        descriptor.RedirectUris.UnionWith(uris);
        descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
            Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code]);
        descriptor.Permissions.UnionWith(scopes.Select(x => Permissions.Prefixes.Scope + x));
        descriptor.AddResourcePermissions(address.Resource);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        // The database transaction covers both the hashed OAuth client secret and its application permissions.
        await using var transaction = await db.Database.BeginTransactionAsync();
        var application = await applications.CreateAsync(descriptor);
        try { db.AssistantConnections.Add(connection); await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch { await applications.DeleteAsync(application); throw; }
        await library.AuditAssistantAsync(new(actor, connection.Id), "connection.register", "created");
        return new(connection, secret);
    }
}
public static class McpSecurity
{
    public const string Bearer = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    public const string Server = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    public static void AddDocumentMcpSecurity(this IServiceCollection services, McpAddress address, bool development)
    {
        services.AddSingleton(address);
        services.AddScoped<NormalizeAssistantScopes>();
        services.AddHttpContextAccessor(); services.AddScoped<AssistantConnections>();
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, McpSigningKeys>();
        services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<CatalogDb>())
            .AddServer(options =>
            {
                options.SetIssuer(address.Issuer).SetAuthorizationEndpointUris("/connect/authorize").SetTokenEndpointUris("/connect/token").SetRevocationEndpointUris("/connect/revoke")
                    .SetConfigurationEndpointUris("/.well-known/oauth-authorization-server", "/.well-known/openid-configuration");
                options.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                options.RegisterScopes(LibraryService.AssistantScopes);
                options.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler=>handler
                    .SetOrder(OpenIddictServerHandlers.Authentication.ValidateScopes.Descriptor.Order-1000)
                    .UseScopedHandler<NormalizeAssistantScopes>());
                options.RegisterResources(address.Resource);
                options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2)).SetAccessTokenLifetime(TimeSpan.FromMinutes(10)).SetRefreshTokenLifetime(TimeSpan.FromDays(7)).SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                options.Configure(o => o.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));
                var host = options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
                if (development) host.DisableTransportSecurityRequirement();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer(); options.UseAspNetCore().DisableAccessTokenExtractionFromBodyForm().DisableAccessTokenExtractionFromQueryString(); options.AddAudiences(address.Resource);
                options.EnableTokenEntryValidation(); options.EnableAuthorizationEntryValidation();
                options.Configure(o => o.TokenValidationParameters.ClockSkew = TimeSpan.Zero);
            });
        services.AddAuthentication(options => options.DefaultAuthenticateScheme = "DocumentStorage.Request")
            .AddPolicyScheme("DocumentStorage.Request", null, options => options.ForwardDefaultSelector = context => context.Request.Path.StartsWithSegments("/mcp") ? Bearer : IdentityConstants.ApplicationScheme);
        services.AddScoped<IAuthorizationHandler, McpActiveHandler>();
        services.AddAuthorization(options =>
        {
            foreach (var scope in LibraryService.AssistantScopes)
                options.AddPolicy(scope, new AuthorizationPolicyBuilder(Bearer).RequireAuthenticatedUser().AddRequirements(new McpActiveRequirement()).RequireAssertion(c => c.User.HasScope(scope)).Build());
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            var oauthLimits = PartitionedRateLimiter.Create<HttpContext,string>(context =>
                context.Request.Path.StartsWithSegments("/connect") || context.Request.Path.StartsWithSegments("/.well-known")
                    ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other"));
            var concurrency = PartitionedRateLimiter.Create<HttpContext,string>(context =>
                context.Request.Path.StartsWithSegments("/mcp") || context.Request.Path.StartsWithSegments("/connect")
                    ? RateLimitPartition.GetConcurrencyLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 8, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other"));
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(oauthLimits, concurrency);
            options.AddPolicy("mcp", context => RateLimitPartition.GetFixedWindowLimiter(context.User.GetClaim("ds_connection") ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 90, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("oauth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 40, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = (context, _) => { context.HttpContext.Response.Headers.RetryAfter = "60"; return ValueTask.CompletedTask; };
        });
    }
}
