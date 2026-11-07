using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DocumentStorage.Web.Mcp;

// Some MCP clients ask for every advertised scope, plus the generic "openid" scope.
// OAuth permits issuing a narrower grant. Normalize known requests BEFORE OpenIddict's
// scope validation, using the intersection of the connection and OAuth registration.
// Keep all normal client, redirect, PKCE, permission, resource and consent checks enabled.
public sealed class NormalizeAssistantScopes(IDbContextFactory<CatalogDb> factory,IOpenIddictApplicationManager applications)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateAuthorizationRequestContext context)
    {
        var requested=context.Request.GetScopes();
        if(string.IsNullOrEmpty(context.ClientId)||requested.Length==0)return;
        // Unknown permissions remain errors; never turn an arbitrary request into a valid grant.
        if(requested.Any(x=>x!=Scopes.OpenId&&x!=Scopes.OfflineAccess&&!LibraryService.AssistantScopes.Contains(x)))return;
        await using var db=await factory.CreateDbContextAsync(context.CancellationToken);
        var connection=await db.AssistantConnections.AsNoTracking().SingleOrDefaultAsync(x=>x.ClientId==context.ClientId,context.CancellationToken);
        if(connection==null||connection.RevokedAt!=null)return;
        var application=await applications.FindByClientIdAsync(context.ClientId,context.CancellationToken);
        if(application==null)return;
        var permissions=await applications.GetPermissionsAsync(application,context.CancellationToken);
        var allowed=JsonSerializer.Deserialize<string[]>(connection.ScopesJson)??[];
        // Stash is an OAuth MCP resource, not an OpenID identity provider. Do not request/issue
        // an ID token just because a client added its generic openid default.
        context.Request.Scope=string.Join(' ',requested.Where(x=>x==Scopes.OfflineAccess ||
            x!=Scopes.OpenId&&allowed.Contains(x)&&permissions.Contains(Permissions.Prefixes.Scope+x)));
    }
}
