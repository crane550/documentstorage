using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using DocumentStorage.Data;

namespace DocumentStorage.Web.Updates;

public static class UpdateEndpoints
{
    public static void MapUpdates(this WebApplication app)
    {
        // Explicit cookie scheme prevents an MCP bearer token from ever starting a deployment.
        var policy = new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser().RequireRole(IdentitySetup.Administrator).Build();
        app.MapGet("/updates/status", async (UpdateService updates) => Results.Ok(await updates.SnapshotAsync())).RequireAuthorization(policy);
        foreach (var mode in new[] { "check", "update" })
        {
            var action = mode;
            app.MapPost("/updates/" + action, async Task<IResult> (HttpContext context, IAntiforgery antiforgery, UpdateService updates, UserManager<LibraryUser> users) =>
            {
                var user = await users.GetUserAsync(context.User);
                if (user == null || !await users.IsInRoleAsync(user, IdentitySetup.Administrator) || await users.IsLockedOutAsync(user)) return Results.Forbid();
                if (context.Request.ContentLength > 4096) return Results.StatusCode(413);
                if (!context.Request.HasFormContentType) return Results.BadRequest("Reload the Updates page and try again.");
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { return Results.BadRequest("Reload the Updates page and try again."); }
                var form = await context.Request.ReadFormAsync();
                try
                {
                    await updates.RequestAsync(action, form["target"].ToString(), form["checkId"].ToString(), context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
                    return Results.LocalRedirect("/settings?tab=updates");
                }
                catch (UpdateException ex) { return Results.LocalRedirect("/settings?tab=updates&error=" + Uri.EscapeDataString(ex.Message)); }
            }).RequireAuthorization(policy);
        }
    }
}
