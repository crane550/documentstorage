using Microsoft.AspNetCore.Components;

namespace DocumentStorage.Web;

/// <summary>
/// For components that only read <see cref="WorkspaceState"/>: Blazor does not re-render a child whose
/// parameters did not change, so these repaint themselves whenever the overview is refreshed.
/// </summary>
public abstract class WorkspaceComponent : ComponentBase, IDisposable
{
    [Inject] protected WorkspaceState Workspace { get; set; } = default!;
    protected override void OnInitialized() => Workspace.Changed += Repaint;
    private void Repaint() => InvokeAsync(StateHasChanged);
    public virtual void Dispose() { Workspace.Changed -= Repaint; GC.SuppressFinalize(this); }
}
