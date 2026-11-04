using DocumentStorage.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace DocumentStorage.Web;

public sealed class LibraryAuthenticationStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LibraryUser>>();
        var user = await users.GetUserAsync(state.User);
        return user != null && !await users.IsLockedOutAsync(user) &&
            state.User.FindFirstValue(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType) == await users.GetSecurityStampAsync(user);
    }
}
