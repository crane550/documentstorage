using DocumentStorage.Core;
namespace DocumentStorage.Web;
public sealed class PassageIndexWorker(LibraryService library,ILogger<PassageIndexWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try{var indexed=await library.IndexNextPassagesAsync(token);await Task.Delay(indexed?200:3000,token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Passage indexing paused: {Kind}",ex.GetType().Name);try{await Task.Delay(5000,token);}catch(OperationCanceledException){break;}}
        }
    }
}
