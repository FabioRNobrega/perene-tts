using Microsoft.AspNetCore.Components;
using PereneTts.Services;
namespace PereneTts.Components;

public abstract class StudioComponent : ComponentBase, IDisposable
{
    [Inject] protected StudioSession Studio { get; set; } = default!;
    protected override void OnInitialized() => Studio.Changed += RefreshView;
    private void RefreshView() => _ = InvokeAsync(StateHasChanged);
    public void Dispose() => Studio.Changed -= RefreshView;
}
