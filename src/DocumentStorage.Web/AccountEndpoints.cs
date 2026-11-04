using DocumentStorage.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace DocumentStorage.Web;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/account/login", async Task<IResult> (HttpContext context, IAntiforgery antiforgery, SignInManager<LibraryUser> signIn) =>
        {
            if (!await ValidFormAsync(context, antiforgery)) return Results.BadRequest("Reload the login page and try again.");
            var form = await context.Request.ReadFormAsync();
            var name = form["username"].ToString().Trim();
            var password = form["password"].ToString();
            var target = LocalTarget(form["returnUrl"].ToString());
            if (name.Length is > 0 and <= 256 && password.Length is > 0 and <= 1024)
            {
                var result = await signIn.PasswordSignInAsync(name, password, isPersistent: true, lockoutOnFailure: true);
                if (result.Succeeded) return Results.LocalRedirect(target);
            }
            return Results.LocalRedirect("/login?failed=true&returnUrl=" + Uri.EscapeDataString(target));
        }).AllowAnonymous();

        app.MapPost("/account/logout", async Task<IResult> (HttpContext context, IAntiforgery antiforgery, SignInManager<LibraryUser> signIn) =>
        {
            if (!await ValidFormAsync(context, antiforgery)) return Results.BadRequest("Reload the account page and try again.");
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/login");
        });

        app.MapPost("/account/change-password", async Task<IResult> (HttpContext context, IAntiforgery antiforgery, UserManager<LibraryUser> users, SignInManager<LibraryUser> signIn) =>
        {
            if (!await ValidFormAsync(context, antiforgery)) return Results.BadRequest("Reload the account page and try again.");
            var form = await context.Request.ReadFormAsync();
            var current = form["currentPassword"].ToString();
            var password = form["newPassword"].ToString();
            var confirm = form["confirmPassword"].ToString();
            if (password != confirm || password.Length is < 12 or > 1024 || current.Length > 1024)
                return Results.LocalRedirect("/account?failed=true");
            var user = await users.GetUserAsync(context.User);
            if (user == null) return Results.Unauthorized();
            var result = await users.ChangePasswordAsync(user, current, password);
            if (!result.Succeeded) return Results.LocalRedirect("/account?failed=true");
            await signIn.RefreshSignInAsync(user);
            return Results.LocalRedirect("/account?changed=true");
        });
    }

    public static string LocalTarget(string? value) => !string.IsNullOrEmpty(value) && value[0] == '/' &&
        (value.Length == 1 || (value[1] != '/' && value[1] != '\\')) && !value.Any(char.IsControl) && !value.Contains("://", StringComparison.Ordinal)
        ? value : "/";

    private static async Task<bool> ValidFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!context.Request.HasFormContentType) return false;
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
