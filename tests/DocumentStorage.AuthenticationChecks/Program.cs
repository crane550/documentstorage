using System.Net;
using System.Text.RegularExpressions;
using DocumentStorage.Data;
using DocumentStorage.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DocumentStorage.Web.Updates;

namespace DocumentStorage.AuthenticationChecks;

internal static partial class Runner
{
    public static async Task Main(string[] args)
    {
        if (args is ["--updater-browser"]) { await UpdateBrowserFixture.RunAsync(); return; }
        const string connection = "Host=127.0.0.1;Port=55432;Database=documentstorage_authentication_checks;Username=postgres";
        const string username = "fixture-admin";
        const string password = "Start-42"; // Synthetic eight-character bootstrap password.
        const string newPassword = "Fresh-42"; // Exercise the new minimum through the real form.
        var keyDirectory = Path.Combine(Path.GetTempPath(), "documentstorage-auth-" + Guid.NewGuid());
        Directory.CreateDirectory(keyDirectory);
        var updateDirectory = Path.Combine(keyDirectory, "updater"); Directory.CreateDirectory(Path.Combine(updateDirectory, "requests"));
        var updateHost = new SyntheticUpdateHost(updateDirectory);
        var values = new Dictionary<string, string?>
        {
            ["Hosting__PublicOrigin"] = "https://documents.example.test",
            ["Hosting__DataProtectionDirectory"] = keyDirectory,
            ["BootstrapAdmin__Username"] = username,
            ["BootstrapAdmin__Password"] = password,
            ["ConnectionStrings__Catalog"] = connection,
            ["DOCUMENTSTORAGE_INSTANCE"] = null,
            ["DOCUMENTSTORAGE_DB_PASSWORD"] = null
        };
        var saved = values.Keys.ToDictionary(x => x, Environment.GetEnvironmentVariable);
        foreach (var item in values) Environment.SetEnvironmentVariable(item.Key, item.Value);
        var options = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(connection).Options;
        var passed = 0;
        void Check(bool valid, string what)
        {
            if (!valid) throw new Exception("FAIL " + what);
            Console.WriteLine("PASS " + what); passed++;
        }
        try
        {
            var invalid = new ConfigurationBuilder().Build();
            try { PublicHosting.Read(invalid, false); throw new Exception("Production unexpectedly allowed missing hostname"); }
            catch (InvalidOperationException) { Check(true, "production requires an explicit public hostname"); }
            await using (var db = new CatalogDb(options)) await db.Database.EnsureDeletedAsync();
            await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root != null && !File.Exists(Path.Combine(root.FullName, "DocumentStorage.sln"))) root = root.Parent;
                builder.UseContentRoot(Path.Combine(root?.FullName ?? throw new InvalidOperationException("Cannot find solution root."), "src", "DocumentStorage.Web"));
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services => services.AddSingleton<IUpdateHost>(updateHost));
            });
            HttpClient NewClient(bool cookies = true) => factory.CreateClient(new WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false, HandleCookies = cookies, BaseAddress = new Uri("https://documents.example.test") });
            using var client = NewClient();
            Check((await client.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "anonymous health check remains available");
            Check((await client.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "anonymous workspace redirects to built-in login");
            Check((await client.GetAsync("/folders")).StatusCode == HttpStatusCode.Redirect, "anonymous folder management redirects to built-in login");
            Check((await client.GetAsync("/recovery")).StatusCode == HttpStatusCode.Redirect, "anonymous recovery workspace redirects to built-in login");
            Check((await client.GetAsync("/ask")).StatusCode == HttpStatusCode.Redirect, "anonymous document conversations redirect to built-in login");
            Check((await client.GetAsync("/folder-planner")).StatusCode == HttpStatusCode.Redirect, "anonymous folder planning redirects to built-in login");
            Check((await client.GetAsync("/app.css")).StatusCode == HttpStatusCode.OK, "login stylesheet is available without authentication");
            Check((await client.GetAsync("/documents/00000000-0000-0000-0000-000000000001/download")).StatusCode == HttpStatusCode.Unauthorized, "anonymous document downloads rejected");
            Check((await client.GetAsync("/documents/00000000-0000-0000-0000-000000000001/preview?v=test")).StatusCode == HttpStatusCode.Unauthorized, "anonymous binary previews rejected");
            Check((await client.GetAsync("/documents/00000000-0000-0000-0000-000000000001/preview-text?v=test")).StatusCode == HttpStatusCode.Unauthorized, "anonymous text previews rejected");
            Check((await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode == HttpStatusCode.Unauthorized, "anonymous Blazor connection rejected");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/"))
            {
                request.Headers.Add("Cf-Access-Jwt-Assertion", "not-an-authentication-method");
                Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Redirect, "Cloudflare headers cannot authenticate a user");
            }
            var form = new Dictionary<string,string> { ["username"] = username, ["password"] = password, ["returnUrl"] = "/" };
            Check((await client.PostAsync("/account/login", new FormUrlEncodedContent(form))).StatusCode == HttpStatusCode.BadRequest, "login requires antiforgery token");
            async Task<string> Token(HttpClient browser, string route)
            {
                var response = await browser.GetAsync(route);
                if (response.StatusCode != HttpStatusCode.OK) throw new Exception("Form page failed: " + route + " " + response.StatusCode);
                var html = await response.Content.ReadAsStringAsync();
                var match = Antiforgery().Match(html);
                if (!match.Success) throw new Exception("Missing antiforgery field on " + route);
                return WebUtility.HtmlDecode(match.Groups[1].Value);
            }
            async Task<HttpResponseMessage> Login(HttpClient browser, string secret, string returnUrl = "/")
            {
                var token = await Token(browser, "/login");
                return await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string>
                { ["username"] = username, ["password"] = secret, ["returnUrl"] = returnUrl, ["__RequestVerificationToken"] = token }));
            }
            var loginToken = await Token(client, "/login");
            Check(loginToken.Length > 0, "login renders a working form without a Blazor connection");
            var wrong = await Login(client, "not-the-password");
            Check(wrong.StatusCode == HttpStatusCode.Redirect && wrong.Headers.Location!.ToString().Contains("failed=true"), "incorrect password rejected");
            var login = await Login(client, password);
            Check(login.StatusCode == HttpStatusCode.Redirect && login.Headers.Location!.ToString() == "/", "correct credentials sign in through the app");
            var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(".DocumentStorage.", StringComparison.Ordinal));
            Check(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase), "public login cookie is Secure HttpOnly and SameSite Lax");
            Check(cookie.StartsWith(".DocumentStorage.documentstorage_authentication_checks=", StringComparison.Ordinal), "cookie name is isolated from other apps and databases");
            await PreviewHttpChecks.RunAsync(factory.Services, client, keyDirectory, Check);
            using (var anonymous = NewClient(false)) await UpdateHttpChecks.RunAsync(factory.Services, client, anonymous, updateHost, await Token(client, "/account"), Check);
            var oldCookie = cookie.Split(';')[0];
            Check((await client.GetAsync("/")).StatusCode == HttpStatusCode.OK, "signed-in workspace opens");
            Check((await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode == HttpStatusCode.OK, "signed-in user can negotiate Blazor connection");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/"))
            {
                request.Headers.Host = "unconfigured.example.test";
                Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.BadRequest, "unconfigured host rejected");
            }
            Check((await client.PostAsync("/account/logout", new FormUrlEncodedContent([]))).StatusCode == HttpStatusCode.BadRequest, "sign-out requires antiforgery token");
            Check((await client.PostAsync("/account/change-password", new FormUrlEncodedContent([]))).StatusCode == HttpStatusCode.BadRequest, "password change requires antiforgery token");
            async Task<HttpResponseMessage> Change(string current, string next, string confirm)
            {
                var token = await Token(client, "/account");
                return await client.PostAsync("/account/change-password", new FormUrlEncodedContent(new Dictionary<string,string>
                { ["currentPassword"] = current, ["newPassword"] = next, ["confirmPassword"] = confirm, ["__RequestVerificationToken"] = token }));
            }
            Check((await Change(password, "Short-7", "Short-7")).Headers.Location!.ToString().Contains("failed=true"), "passwords below eight characters are refused");
            Check((await Change(password, newPassword, "mismatched-confirmation")).Headers.Location!.ToString().Contains("failed=true"), "password confirmation mismatch refused");
            Check((await Change("incorrect-current", newPassword, newPassword)).Headers.Location!.ToString().Contains("failed=true"), "password change checks the current password");
            Check((await Change(password, newPassword, newPassword)).Headers.Location!.ToString() == "/account?changed=true", "password change succeeds and refreshes the current sign-in");
            using (var oldSession = NewClient(false))
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/"))
            {
                request.Headers.Add("Cookie", oldCookie);
                Check((await oldSession.SendAsync(request)).StatusCode == HttpStatusCode.Redirect, "password change invalidates earlier session cookies");
            }
            var logoutToken = await Token(client, "/account");
            Check((await client.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = logoutToken }))).Headers.Location!.ToString() == "/login", "sign-out returns to the app login");
            Check((await client.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "signed-out browser loses workspace access");
            Check((await Login(client, password)).Headers.Location!.ToString().Contains("failed=true"), "old password no longer signs in");
            Check((await Login(client, newPassword, "https://outside.example.test/")).Headers.Location!.ToString() == "/", "login refuses external return URLs");
            Check(AccountEndpoints.LocalTarget("//outside.example.test") == "/" && AccountEndpoints.LocalTarget("/\\outside.example.test") == "/", "protocol-relative and backslash redirects refused");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<LibraryUser>>();
                var user = (await users.FindByNameAsync(username))!;
                Check(user.PasswordHash != newPassword && await users.IsInRoleAsync(user, IdentitySetup.Administrator), "first admin uses a password hash and administrator role");
                var replacement = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
                { ["BootstrapAdmin:Username"] = username, ["BootstrapAdmin:Password"] = password }).Build();
                await IdentitySetup.SeedAdministratorAsync(scope.ServiceProvider, replacement);
                Check(await users.CheckPasswordAsync(user, newPassword) && !await users.CheckPasswordAsync(user, password), "bootstrap never resets existing passwords on restart");
                await users.ResetAccessFailedCountAsync(user);
                await users.SetLockoutEndDateAsync(user, null);
            }
            using (var attacker = NewClient())
            {
                for (var i = 0; i < 5; i++) await Login(attacker, "wrong-attempt");
                Check((await Login(attacker, newPassword)).Headers.Location!.ToString().Contains("failed=true"), "five failed attempts lock out even the correct password temporarily");
            }
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<LibraryUser>>();
                var user = (await users.FindByNameAsync(username))!;
                Check(await users.IsLockedOutAsync(user) && user.LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(14), "lockout lasts 15 minutes");
            }
            Console.WriteLine($"All {passed} application authentication checks passed.");
        }
        finally
        {
            foreach (var item in saved) Environment.SetEnvironmentVariable(item.Key, item.Value);
            await using var db = new CatalogDb(options);
            await db.Database.EnsureDeletedAsync();
            Directory.Delete(keyDirectory, true);
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex Antiforgery();
}
