using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DocumentStorage.Web;

public sealed record AccessHosting(Uri? PublicOrigin, string? TeamDomain, string? Audience)
{
    public bool Enabled => PublicOrigin != null;

    public static AccessHosting Read(IConfiguration configuration, bool development)
    {
        var origin = configuration["Hosting:PublicOrigin"];
        var team = configuration["CloudflareAccess:TeamDomain"];
        var audience = configuration["CloudflareAccess:Audience"];
        if (string.IsNullOrWhiteSpace(origin))
        {
            if (!development || !string.IsNullOrWhiteSpace(team) || !string.IsNullOrWhiteSpace(audience))
                throw new InvalidOperationException("Production hosting requires Hosting:PublicOrigin and CloudflareAccess:TeamDomain/Audience. Local unauthenticated use is Development-only.");
            return new(null, null, null);
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" ||
            uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" || uri.Port != 443 || uri.HostNameType != UriHostNameType.Dns)
            throw new InvalidOperationException("Hosting:PublicOrigin must be an HTTPS origin with no path, query, credentials or custom port.");
        team = team?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(team) || !team.EndsWith(".cloudflareaccess.com", StringComparison.Ordinal) ||
            Uri.CheckHostName(team) != UriHostNameType.Dns || team[..^".cloudflareaccess.com".Length].Contains('.') ||
            string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException("Set the Cloudflare Access team hostname (team.cloudflareaccess.com) and this application's Audience (AUD) tag.");
        return new(uri, team, audience.Trim());
    }

    public void AddAuthentication(IServiceCollection services)
    {
        if (!Enabled) return;
        var issuer = "https://" + TeamDomain;
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.RequireHttpsMetadata = true;
            options.RefreshOnIssuerKeyNotFound = true;
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                issuer + "/cdn-cgi/access/certs", new AccessKeysRetriever(issuer), new HttpDocumentRetriever { RequireHttps = true });
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = issuer,
                ValidateAudience = true, ValidAudience = Audience,
                ValidateLifetime = true, RequireExpirationTime = true,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "email"
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var values = context.Request.Headers["Cf-Access-Jwt-Assertion"];
                    if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 16384) context.NoResult();
                    else context.Token = values[0];
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
    }

    public bool AllowsHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) ||
        (Enabled && host.Equals(PublicOrigin!.Host, StringComparison.OrdinalIgnoreCase));
}

// Cloudflare publishes JWKS directly, rather than an OpenID Connect discovery document.
public sealed class AccessKeysRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        if (address != issuer + "/cdn-cgi/access/certs") throw new InvalidOperationException("Unexpected Access key endpoint.");
        var keys = new JsonWebKeySet(await retriever.GetDocumentAsync(address, cancel));
        var result = new OpenIdConnectConfiguration { Issuer = issuer };
        foreach (var key in keys.GetSigningKeys()) result.SigningKeys.Add(key);
        if (result.SigningKeys.Count == 0) throw new InvalidOperationException("Cloudflare returned no signing keys.");
        return result;
    }
}
