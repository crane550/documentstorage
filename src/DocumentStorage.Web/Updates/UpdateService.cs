using System.Diagnostics;
using System.Text.Json;

namespace DocumentStorage.Web.Updates;

public sealed record UpdateRequest(string Id, string Mode, string Running, string Target, string CheckId, string Actor, DateTimeOffset RequestedAt);
public sealed record PendingCommit(string Commit, string Subject);
public sealed record UpdateStatus(string Id, string Mode, string State, string Stage, string Running, string Latest,
    DateTimeOffset UpdatedAt, List<PendingCommit> Pending, bool DatabaseChange = false, bool MoreChanges = false)
{
    public bool Active => State is "Queued" or "Running";
}
public sealed record UpdateSnapshot(bool Installed, string Version, string Build, string? Commit, UpdateStatus? Status, UpdateStatus? Check);
public sealed class UpdateException(string message) : Exception(message);

// The only privileged process the web app may start is the preinstalled fixed Windows task.
public interface IUpdateHost
{
    string Directory { get; }
    Task<bool> InstalledAsync();
    Task StartAsync();
}
public sealed class WindowsUpdateHost : IUpdateHost
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private DateTimeOffset checkedAt;
    private bool installed;
    public string Directory => Path.Combine(root, "updater");
    private string TaskName => Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + " updater";
    public async Task<bool> InstalledAsync()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(Directory, "installed.json"))) return false;
        if (DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromSeconds(30)) return installed;
        installed = await RunAsync("/Query", "/TN", TaskName) == 0;
        checkedAt = DateTimeOffset.UtcNow;
        return installed;
    }
    public async Task StartAsync()
    {
        if (await RunAsync("/Run", "/TN", TaskName) != 0)
            throw new UpdateException("Windows could not start the updater. Re-run the updater installer if the task account's password changed.");
    }
    private static async Task<int> RunAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error); return process.ExitCode; }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); return -1; }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return -1; }
    }
}

public sealed class UpdateService(IUpdateHost host, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string RequestFile => Path.Combine(host.Directory, "requests", "pending.json");
    private async Task<UpdateStatus?> ReadAsync(string name)
    {
        try
        {
            await using var stream = new FileStream(Path.Combine(host.Directory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 65536) return null;
            return await JsonSerializer.DeserializeAsync<UpdateStatus>(stream, Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public async Task<UpdateSnapshot> SnapshotAsync()
    {
        var status = await ReadAsync("status.json");
        // A durable pending request bridges the gap between the POST and task startup.
        if (File.Exists(RequestFile))
        {
            try
            {
                await using var stream = new FileStream(RequestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length <= 4096 && await JsonSerializer.DeserializeAsync<UpdateRequest>(stream, Json) is {} request)
                    status = new(request.Id, request.Mode, "Queued", "Waiting for the Windows updater", request.Running, request.Target, request.RequestedAt, []);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return new(await host.InstalledAsync(), AppVersion.Display, AppVersion.Build, AppVersion.Commit, status, await ReadAsync("check.json"));
    }
    public async Task RequestAsync(string mode, string target, string checkId, string actor)
    {
        if (mode is not ("check" or "update") || string.IsNullOrWhiteSpace(actor) || actor.Length > 256) throw new UpdateException("Invalid update request.");
        await gate.WaitAsync();
        try
        {
            var snapshot = await SnapshotAsync();
            if (!snapshot.Installed) throw new UpdateException("Install the Windows updater once before updating from the browser.");
            if (!AppVersion.ValidCommit(AppVersion.Commit)) throw new UpdateException("This build has no Git revision. Deploy a clean Git checkout once first.");
            if (snapshot.Status?.Active == true || File.Exists(RequestFile)) throw new UpdateException("An update request is already in progress. Wait for its result; do not start another deployment.");
            if (mode == "update")
            {
                var check = snapshot.Check;
                if (check is not { Mode: "check", State: "Succeeded" } || check.Id != checkId || check.Running != AppVersion.Commit ||
                    !AppVersion.ValidCommit(target) || check.Latest != target || target == AppVersion.Commit ||
                    time.GetUtcNow() - check.UpdatedAt > TimeSpan.FromMinutes(30) || check.UpdatedAt > time.GetUtcNow().AddMinutes(1))
                    throw new UpdateException("Check for updates again before approving this release.");
            }
            else { target = ""; checkId = ""; }
            var request = new UpdateRequest(Guid.NewGuid().ToString("N"), mode, AppVersion.Commit!, target, checkId, actor, time.GetUtcNow());
            // Write to a temp file, then rename without overwrite. The task never sees partial JSON.
            var temp = Path.Combine(host.Directory, "requests", request.Id + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(request, Json));
                File.Move(temp, RequestFile, overwrite: false);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            // Preserve the request on an uncertain Windows response; retrying could start a second deployment.
            await host.StartAsync();
        }
        catch (IOException) { throw new UpdateException("The updater could not accept this request. Check its installation and folder permissions."); }
        catch (UnauthorizedAccessException) { throw new UpdateException("The app cannot write updater requests. Re-run the updater installer to repair permissions."); }
        finally { gate.Release(); }
    }
}
