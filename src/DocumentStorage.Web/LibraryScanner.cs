using DocumentStorage.Core;

namespace DocumentStorage.Web;

public sealed class LibraryScanner(LibraryService library, ILogger<LibraryScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await library.ScanAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Library scan failed. The next scheduled scan will retry."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
