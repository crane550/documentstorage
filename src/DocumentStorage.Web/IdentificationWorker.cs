using DocumentStorage.Core;

namespace DocumentStorage.Web;

public sealed class IdentificationWorker(LibraryService library, IOllamaClient client, ILogger<IdentificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await library.RecoverIdentificationAsync();
        await library.RecoverDeskAsync();
        var offset = 0; var nextAutomatic = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextAutomatic)
                {
                    offset = await library.QueueAutomaticIdentificationAsync(offset);
                    nextAutomatic = DateTimeOffset.UtcNow.AddSeconds(15);
                }
                var processed = await library.ProcessNextDeskAsync(client, stoppingToken);
                if (!processed) processed = await library.ProcessNextIdentificationAsync(client, stoppingToken);
                await Task.Delay(processed ? 100 : 2000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Identification worker failed; saved jobs remain available for recovery.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
