using DocumentStorage.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Web;

public static class IdentitySetup
{
    public const string Administrator = "Administrator";

    public static void AddLibraryIdentity(this IServiceCollection services, PublicHosting hosting, string database)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.ReturnUrlParameter = "returnUrl";
            options.Cookie.Name = ".DocumentStorage." + database;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = hosting.Enabled ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/_blazor") || context.Request.Path.StartsWithSegments("/documents"))
                    context.Response.StatusCode = 401;
                else context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });
        foreach (var scheme in new[] { IdentityConstants.ExternalScheme, IdentityConstants.TwoFactorRememberMeScheme, IdentityConstants.TwoFactorUserIdScheme })
            services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(scheme, options =>
            {
                options.Cookie.Name = ".DocumentStorage." + database + "." + scheme;
                options.Cookie.SecurePolicy = hosting.Enabled ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            });
        services.AddIdentityCore<LibraryUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireDigit = false;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<CatalogDb>().AddSignInManager().AddDefaultTokenProviders();
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().RequireRole(Administrator).Build());
        services.AddCascadingAuthenticationState();
        services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, LibraryAuthenticationStateProvider>();
    }

    public static async Task SeedAdministratorAsync(IServiceProvider services, IConfiguration configuration)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Administrator))
        {
            var createdRole = await roles.CreateAsync(new IdentityRole(Administrator));
            if (!createdRole.Succeeded) throw new InvalidOperationException("Could not create the administrator role.");
        }
        var users = services.GetRequiredService<UserManager<LibraryUser>>();
        if (await users.Users.AnyAsync()) return; // Bootstrap never resets an existing user's password.
        var name = configuration["BootstrapAdmin:Username"]?.Trim();
        var password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("No administrator exists. Supply BootstrapAdmin:Username and BootstrapAdmin:Password through the installer or private configuration for the first startup.");
        var admin = new LibraryUser { UserName = name, LockoutEnabled = true };
        var created = await users.CreateAsync(admin, password);
        if (!created.Succeeded) throw new InvalidOperationException("Administrator setup failed: " + string.Join(" ", created.Errors.Select(x => x.Description)));
        var assigned = await users.AddToRoleAsync(admin, Administrator);
        if (!assigned.Succeeded)
        {
            await users.DeleteAsync(admin);
            throw new InvalidOperationException("Could not assign the administrator role. Restart to retry setup.");
        }
    }
}
