using Photino.NET;
using PereneTts.Services;
namespace PereneTts.Desktop;

/// <summary>Desktop shell: native panels on the window's main thread, and close requests from <see cref="CloseCoordinator"/>.
/// The app is not sandboxed, so picked files are readable without extra permission prompts.</summary>
public sealed class PhotinoHostShell(PhotinoWindow window, CloseCoordinator coordinator, string saveDirectory) : IHostShell
{
    public bool IsDesktop => true;

    // Photino's save panel cannot suggest a file name, so this one is opened through AppKit directly.
    public Task<string?> PickSaveFileAsync(string suggestedName, FileFilter filter) => Task.Run(() =>
    {
        string? path = null;
        window.Invoke(() => path = MacAppKit.ShowSavePanel("Save", saveDirectory, suggestedName));
        return path;
    });

    public async Task<IReadOnlyList<string>> PickFilesAsync(FileFilter filter, bool multiple) =>
        await window.ShowOpenFileAsync(multiple ? "Choose files" : "Choose a file", null, multiple,
            [(filter.Name, filter.Extensions.ToArray())]) ?? [];

    public async Task<string?> PickFolderAsync() =>
        (await window.ShowOpenFolderAsync("Choose folder"))?.FirstOrDefault(path => !string.IsNullOrEmpty(path));

    public event Func<CloseRequest, Task>? CloseRequested
    {
        add => coordinator.CloseRequested += value;
        remove => coordinator.CloseRequested -= value;
    }

    public void CompleteClose() => coordinator.CompleteClose();
}
