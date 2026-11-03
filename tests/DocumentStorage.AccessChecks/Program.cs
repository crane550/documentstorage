using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentStorage.Data;
using DocumentStorage.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DocumentStorage.AccessChecks;

internal static class Runner
{
    public static async Task Main()
    {
        const string issuer = "https://fixture.cloudflareaccess.com";
        const string audience = "synthetic-app-audience";
        const string connection = "Host=127.0.0.1;Port=55432;Database=documentstorage_access_checks;Username=postgres";
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "fixture-key" };
        var config = new OpenIdConnectConfiguration { Issuer = issuer };
        config.SigningKeys.Add(key);
        var values = new Dictionary<string, string?>
        {
            ["Hosting__PublicOrigin"] = "https://documents.example.test",
            ["CloudflareAccess__TeamDomain"] = "fixture.cloudflareaccess.com",
            ["CloudflareAccess__Audience"] = audience,
            ["ConnectionStrings__Catalog"] = connection,
            ["DOCUMENTSTORAGE_INSTANCE"] = null,
            ["DOCUMENTSTORAGE_DB_PASSWORD"] = null
        };
        var saved = values.Keys.ToDictionary(x => x, Environment.GetEnvironmentVariable);
        foreach (var item in values) Environment.SetEnvironmentVariable(item.Key, item.Value);
        var passed = 0;
        void Check(bool valid, string what)
        {
            if (!valid) throw new Exception("FAIL " + what);
            Console.WriteLine("PASS " + what); passed++;
        }
        try
        {
            var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>()).Build();
            try { AccessHosting.Read(invalid, false); throw new Exception("Production unexpectedly allowed anonymous hosting"); }
            catch (InvalidOperationException) { Check(true, "production refuses missing authentication configuration"); }
            var dbOptions = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(connection).Options;
            await using (var db = new CatalogDb(dbOptions)) await db.Database.EnsureDeletedAsync();
            await using (var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root != null && !File.Exists(Path.Combine(root.FullName, "DocumentStorage.sln"))) root = root.Parent;
                builder.UseContentRoot(Path.Combine(root?.FullName ?? throw new InvalidOperationException("Cannot find solution root."), "src", "DocumentStorage.Web"));
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config)));
            }))
            {
                using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://documents.example.test") });
                Check((await client.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "health check remains available without document access");
                Check((await client.GetAsync("/")).StatusCode == HttpStatusCode.Unauthorized, "anonymous workspace rejected");
                Check((await client.GetAsync("/app.css")).StatusCode == HttpStatusCode.Unauthorized, "anonymous static asset requests pass through authentication");
                Check((await client.GetAsync("/documents/00000000-0000-0000-0000-000000000001/download")).StatusCode == HttpStatusCode.Unauthorized, "anonymous document downloads rejected");
                Check((await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode == HttpStatusCode.Unauthorized, "anonymous Blazor connection rejected");
                string Token(string aud = audience, string iss = issuer, bool expired = false, SecurityKey? signingKey = null) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                    issuer: iss, audience: aud, claims: [new Claim("email", "fixture@example.test")],
                    notBefore: DateTime.UtcNow.AddMinutes(-10), expires: expired ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow.AddMinutes(5),
                    signingCredentials: new SigningCredentials(signingKey ?? key, SecurityAlgorithms.RsaSha256)));
                async Task<HttpStatusCode> Request(string token, string path = "/")
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, path);
                    request.Headers.Add("Cf-Access-Jwt-Assertion", token);
                    return (await client.SendAsync(request)).StatusCode;
                }
                Check(await Request("not-a-jwt") == HttpStatusCode.Unauthorized, "malformed Access assertion rejected");
                Check(await Request(Token(aud: "another-application")) == HttpStatusCode.Unauthorized, "another application's token rejected");
                Check(await Request(Token(iss: "https://another.cloudflareaccess.com")) == HttpStatusCode.Unauthorized, "another team's token rejected");
                Check(await Request(Token(expired: true)) == HttpStatusCode.Unauthorized, "expired token rejected");
                using var attackerRsa = RSA.Create(2048);
                Check(await Request(Token(signingKey: new RsaSecurityKey(attackerRsa) { KeyId = key.KeyId })) == HttpStatusCode.Unauthorized, "forged signature rejected");
                Check(await Request(Token()) == HttpStatusCode.OK, "valid signed Access token opens workspace");
                using (var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1"))
                {
                    request.Headers.Add("Cf-Access-Jwt-Assertion", Token());
                    Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.OK, "valid token can negotiate interactive Blazor connection");
                }
                using (var request = new HttpRequestMessage(HttpMethod.Get, "/"))
                {
                    request.Headers.Authorization = new("Bearer", Token());
                    Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Unauthorized, "assertion must arrive through the configured Cloudflare header");
                }
                using (var request = new HttpRequestMessage(HttpMethod.Get, "/"))
                {
                    request.Headers.Host = "unconfigured.example.test";
                    request.Headers.Add("Cf-Access-Jwt-Assertion", Token());
                    Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.BadRequest, "unconfigured host rejected even with valid token");
                }
                using (var request = new HttpRequestMessage(HttpMethod.Get, "/"))
                {
                    request.Headers.Add("Cf-Access-Jwt-Assertion", new[] { Token(), Token() });
                    Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Unauthorized, "duplicate assertion headers rejected");
                }
            }
            await using (var db = new CatalogDb(dbOptions)) await db.Database.EnsureDeletedAsync();
            var parameters = rsa.ExportParameters(false);
            var jwks = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = key.KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) } } });
            var retrieved = await new AccessKeysRetriever(issuer).GetConfigurationAsync(issuer + "/cdn-cgi/access/certs", new FixtureDocument(jwks), CancellationToken.None);
            Check(retrieved.SigningKeys.Count == 1 && retrieved.Issuer == issuer, "Cloudflare JWKS response supplies the expected signing keys and issuer");
            Console.WriteLine($"All {passed} public-host authentication checks passed.");
        }
        finally { foreach (var item in saved) Environment.SetEnvironmentVariable(item.Key, item.Value); }
    }

    private sealed class FixtureDocument(string json) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) => Task.FromResult(json);
    }
}
