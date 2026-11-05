using System.Net;
using Microsoft.AspNetCore;
using System.Security.Claims;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DocumentStorage.Web.Mcp;

public static class OAuthEndpoints
{
    private static IResult Deny(string error, string description) => Results.Forbid(new AuthenticationProperties(new Dictionary<string,string?>
    { [OpenIddictServerAspNetCoreConstants.Properties.Error] = error, [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description }), [McpSecurity.Server]);
    public static void MapAssistantOAuth(this WebApplication app, McpAddress address)
    {
        object Metadata() => new { resource = address.Resource, authorization_servers = new[] { address.Issuer.AbsoluteUri.TrimEnd('/') }, scopes_supported = LibraryService.AssistantScopes, bearer_methods_supported = new[] { "header" }, resource_name = "Document Manager MCP" };
        app.MapGet("/.well-known/oauth-protected-resource/mcp", Metadata).AllowAnonymous();
        app.MapGet("/.well-known/oauth-protected-resource", Metadata).AllowAnonymous();
        app.MapMethods("/connect/authorize", ["GET", "POST"], async Task<IResult> (HttpContext context, IAntiforgery antiforgery,
            UserManager<LibraryUser> users, IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations, CatalogDb db) =>
        {
            var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Missing OAuth request.");
            if (request.GetResources().Length != 1 || request.GetResources()[0] != address.Resource) return Deny(Errors.InvalidTarget, "Request the document MCP resource.");
            if (request.CodeChallengeMethod != CodeChallengeMethods.Sha256) return Deny(Errors.InvalidRequest, "S256 PKCE is required.");
            if (request.HasPromptValue(PromptValues.None)) return Deny(Errors.ConsentRequired, "Connection consent is required.");
            var authorizationFields = new[] { "client_id", "redirect_uri", "response_type", "scope", "resource", "state", "code_challenge", "code_challenge_method", "prompt", "max_age" };
            var parameters = request.GetParameters().Where(x => authorizationFields.Contains(x.Key)).ToDictionary(x => x.Key, x => (string?)x.Value.ToString());
            var returnUrl = QueryHelpers.AddQueryString("/connect/authorize", parameters);
            var login = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!login.Succeeded) return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, [IdentityConstants.ApplicationScheme]);
            var user = await users.GetUserAsync(login.Principal!);
            var connection = await db.AssistantConnections.SingleOrDefaultAsync(x => x.ClientId == request.ClientId);
            if (user == null || !await users.IsInRoleAsync(user, IdentitySetup.Administrator) || await users.IsLockedOutAsync(user) || connection == null || connection.RevokedAt != null || connection.OwnerId != user.Id)
                return Deny(Errors.AccessDenied, "This connection is unavailable for the signed-in account.");
            var allowed = JsonSerializer.Deserialize<string[]>(connection.ScopesJson)!;
            if (!request.HasScope("catalog.read") || request.GetScopes().Any(x => x != Scopes.OfflineAccess && !allowed.Contains(x))) return Deny(Errors.InvalidScope, "Requested permissions exceed this connection.");
            if (HttpMethods.IsPost(context.Request.Method))
            {
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { return Results.BadRequest("Reload consent before responding."); }
                if ((await context.Request.ReadFormAsync())["decision"] != "allow") return Deny(Errors.AccessDenied, "The user declined this connection.");
                var identity = new ClaimsIdentity(McpSecurity.Server, Claims.Name, Claims.Role);
                identity.SetClaim(Claims.Subject, user.Id).SetClaim("ds_connection", connection.Id.ToString()).SetClaim("ds_stamp", await users.GetSecurityStampAsync(user));
                identity.SetScopes(request.GetScopes()); identity.SetResources(address.Resource);
                var principal = new ClaimsPrincipal(identity);
                var application = await applications.FindByClientIdAsync(connection.ClientId) ?? throw new InvalidOperationException("OAuth application missing.");
                var authorization = await authorizations.CreateAsync(principal, user.Id, (await applications.GetIdAsync(application))!, AuthorizationTypes.Permanent, request.GetScopes());
                principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization));
                principal.SetDestinations(_ => [Destinations.AccessToken]);
                return Results.SignIn(principal, authenticationScheme: McpSecurity.Server);
            }
            var tokens = antiforgery.GetAndStoreTokens(context);
            static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
            var hidden = string.Join("", parameters.Select(x => "<input type=\"hidden\" name=\"" + H(x.Key) + "\" value=\"" + H(x.Value) + "\">"));
            var labels = new Dictionary<string,string> { ["catalog.read"] = "Read folder names, saved instructions and rule definitions", ["documents.read"] = "Read indexed document text and reviewed facts", ["instructions.propose"] = "Propose instruction changes for your review", ["rules.propose"] = "Propose paused filing rules for your review", ["filing.execute"] = "Request and execute individually approved filing moves", ["sorting.propose"] = "Propose document details, kinds, merges and tidy-up moves for your review", [Scopes.OfflineAccess] = "Maintain the connection with expiring, revocable refresh tokens" };
            var scopes = string.Join("", request.GetScopes().Select(scope => "<li>" + H(labels.GetValueOrDefault(scope, scope)) + "</li>"));
            var html = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Connect assistant</title><link rel=\"stylesheet\" href=\"/app.css\"><link rel=\"stylesheet\" href=\"/account.css\"><main class=\"account-shell\"><section class=\"account-card oauth-consent\"><div class=\"eyebrow\">ASSISTANT CONNECTION</div><h1>Connect " + H(connection.Name) + "?</h1><p>Signed in as " + H(user.UserName) + ". This connection covers all registered storage locations.</p><ul>" + scopes + "</ul><p>Proposed instructions and rules require review in the app. Filing requires approval of an exact preview. Document text is available only with documents.read permission.</p><p>Callback: <code>" + H(request.RedirectUri) + "</code></p><form method=\"post\" action=\"" + H("/connect/authorize") + "\"><input type=\"hidden\" name=\"" + H(tokens.FormFieldName) + "\" value=\"" + H(tokens.RequestToken) + "\">" + hidden + "<button name=\"decision\" value=\"allow\" class=\"button primary\">Allow connection</button><button name=\"decision\" value=\"deny\" class=\"button subtle\">Deny</button></form></section></main></html>";
            return Results.Content(html, "text/html");
        }).AllowAnonymous().RequireRateLimiting("oauth");
        app.MapPost("/connect/token", async Task<IResult> (HttpContext context, UserManager<LibraryUser> users, CatalogDb db) =>
        {
            var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Missing OAuth request.");
            if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType()) return Deny(Errors.UnsupportedGrantType, "Use authorization code or refresh token.");
            if (request.GetResources().Length != 1 || request.GetResources()[0] != address.Resource) return Deny(Errors.InvalidTarget, "Request the original document MCP resource.");
            var result = await context.AuthenticateAsync(McpSecurity.Server);
            if (!result.Succeeded || result.Principal == null) return Deny(Errors.InvalidGrant, "The authorization is no longer valid.");
            var principal = result.Principal;
            var user = await users.FindByIdAsync(principal.GetClaim(Claims.Subject) ?? "");
            if (!Guid.TryParse(principal.GetClaim("ds_connection"), out var id)) return Deny(Errors.InvalidGrant, "Connection not found.");
            var connection = await db.AssistantConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            if (user == null || connection == null || connection.RevokedAt != null || connection.OwnerId != user.Id || connection.ClientId != request.ClientId ||
                principal.GetClaim("ds_stamp") != await users.GetSecurityStampAsync(user) || await users.IsLockedOutAsync(user) || !await users.IsInRoleAsync(user, IdentitySetup.Administrator))
                return Deny(Errors.InvalidGrant, "The user or connection authorization is no longer valid.");
            if (request.IsRefreshTokenGrantType() && request.GetScopes().Length > 0)
            {
                if (request.GetScopes().Any(x => !principal.HasScope(x))) return Deny(Errors.InvalidScope, "Refresh cannot add permissions.");
                principal.SetScopes(request.GetScopes());
            }
            principal.SetResources(address.Resource);
            return Results.SignIn(principal, authenticationScheme: McpSecurity.Server);
        }).AllowAnonymous().RequireRateLimiting("oauth");
    }
}
