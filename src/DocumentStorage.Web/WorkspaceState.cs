using System.Security.Claims;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace DocumentStorage.Web;

/// <summary>
/// One per circuit: the library overview every page and the sidebar read, the signed-in actor, and the notice
/// strip. Pages change files only through LibraryService and then call <see cref="RefreshAsync"/>.
/// </summary>
public sealed class WorkspaceState(LibraryService library, AuthenticationStateProvider authentication, IConfiguration configuration)
{
    public LibraryOverview Overview { get; private set; } = new([], [], [], 0, 0);
    public int PendingProposals { get; private set; }
    public bool ReaderEnabled { get; private set; }
    public bool Loaded { get; private set; }
    public Notice? Current { get; private set; }
    public string DisplayName => configuration["Application:DisplayName"] ?? "Document Manager";
    public event Action? Changed;
    public event Action? NoticeChanged;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? actor;

    public async Task RefreshAsync()
    {
        await gate.WaitAsync();
        try
        {
            Overview = await library.OverviewAsync();
            PendingProposals = (await library.AssistantChangesAsync(await ActorAsync())).Proposals.Count(x => x.State == "Pending");
            ReaderEnabled = (await library.IdentificationOverviewAsync()).Settings.Enabled;
            Loaded = true;
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    public async Task EnsureLoadedAsync() { if (!Loaded) await RefreshAsync(); }

    public async Task<string> ActorAsync() => actor ??= (await authentication.GetAuthenticationStateAsync()).User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public void Notify(string text, bool error = false) { Current = new(text, error, Guid.NewGuid()); NoticeChanged?.Invoke(); }
    public void Dismiss(Guid id) { if (Current?.Id == id) { Current = null; NoticeChanged?.Invoke(); } }
    public void ClearNotice() { if (Current != null) { Current = null; NoticeChanged?.Invoke(); } }

    // Read helpers shared by every page, so a folder is always named the same way.
    public StorageRoot? Root(Guid id) => Overview.Roots.FirstOrDefault(x => x.Id == id);
    public string RootName(Guid id) => Root(id)?.Name ?? "Storage";
    public bool IsOnline(Guid rootId) => Root(rootId)?.IsOnline == true;
    public bool IsBlocked(Guid rootId) => Overview.BlockedRootIds?.Contains(rootId) == true;
    public bool IsWritable(Guid rootId) => IsOnline(rootId) && !IsBlocked(rootId);
    public CatalogEntry? Folder(Guid? id) => id == null ? null : Overview.Folders.FirstOrDefault(x => x.Id == id);
    public CatalogEntry? RootFolder(Guid rootId) => Overview.Folders.FirstOrDefault(x => x.RootId == rootId && x.ParentId == null);
    public string FolderLabel(CatalogEntry folder) => RootName(folder.RootId) + (folder.RelativePath.Length > 0 ? " / " + folder.RelativePath : "");
    public string FolderLabel(Guid id) => Folder(id) is { } folder ? FolderLabel(folder) : "Unavailable folder";
    public List<CatalogEntry> Ancestors(Guid? folderId, bool includeSelf = true)
    {
        var result = new List<CatalogEntry>();
        var current = Folder(folderId);
        if (current != null && !includeSelf) current = Folder(current.ParentId);
        while (current != null) { result.Insert(0, current); current = Folder(current.ParentId); }
        return result;
    }
    public bool InInbox(CatalogEntry folder) => Ancestors(folder.Id).Any(x => x.IsIntake);
    public List<CatalogEntry> InboxFolders => Overview.Folders.Where(x => x.IsIntake && !x.IsMissing && x.TrashBatchId == null).ToList();
    public string InheritedNotes(Guid? folderId)
    {
        var notes = Ancestors(folderId, includeSelf: false).Where(x => x.Instructions.Length > 0).Select(x => x.Name + ":\n" + x.Instructions);
        return string.Join("\n\n", notes);
    }

    public static FilingRule ToEngine(FilingRuleDefinition rule) => new(rule.Id, rule.DocumentType, rule.DestinationFolderId,
        JsonSerializer.Deserialize<Dictionary<string,string>>(rule.MatchJson) ?? [], rule.FilenameTemplate,
        JsonSerializer.Deserialize<string[]>(rule.RequiredFactsJson) ?? []);

    public static string FormatSize(long size) => size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024d:0.#} KB" : $"{size / (1024d * 1024):0.#} MB";
    public static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1) && when.ToLocalTime().Date == DateTime.Today) return when.ToLocalTime().ToString("h:mm tt");
        return when.ToLocalTime().ToString(when.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy");
    }
    public static string Extension(string name) => Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
}

public sealed record Notice(string Text, bool Error, Guid Id);
