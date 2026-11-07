using DocumentStorage.Core;

namespace DocumentStorage.Web;

public sealed class ExtractionWorker(LibraryService library, ILogger<ExtractionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await library.RecoverExtractionAsync();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { var processed = await library.ProcessNextExtractionAsync(stoppingToken); await Task.Delay(processed ? 100 : 1500, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Text extraction worker stopped a cycle: {Kind}", ex.GetType().Name); try { await Task.Delay(3000, stoppingToken); } catch (OperationCanceledException) { break; } }
        }
    }
}
