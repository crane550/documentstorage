using OpenIddict.Abstractions;

namespace DocumentStorage.Web.Mcp;

public sealed class AssistantMaintenance(IServiceScopeFactory scopes, ILogger<AssistantMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
                await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(cutoff, stoppingToken);
                await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(cutoff, stoppingToken);
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("OAuth maintenance failed: {Kind}", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
