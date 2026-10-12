namespace PereneTts.Services;

/// <summary>What the page runs inside: a browser tab (web mode) or the Perene TTS window (desktop mode).
/// Components depend on this, never on the native window library.</summary>
public interface IHostShell
{
    bool IsDesktop { get; }
    /// <summary>Native save dialog; null when the user cancels.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, FileFilter filter);
    /// <summary>Native open dialog; empty when the user cancels.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(FileFilter filter, bool multiple);
    /// <summary>Native folder dialog; null when the user cancels.</summary>
    Task<string?> PickFolderAsync();
    /// <summary>The user asked to close the window; each open studio page decides whether to confirm.</summary>
    event Func<CloseRequest, Task>? CloseRequested;
    /// <summary>Closes the window (and quits) after the page decided it may.</summary>
    void CompleteClose();
}

public sealed record FileFilter(string Name, IReadOnlyList<string> Extensions)
{
    public static readonly FileFilter Mp3 = new("MP3 audio", ["mp3"]);
    public static readonly FileFilter Zip = new("ZIP archive", ["zip"]);
    public static readonly FileFilter Text = new("Text files", ["txt"]);
    public static readonly FileFilter Recording = new("MP3 or WAV recording", ["mp3", "wav"]);
}

/// <summary>One close attempt. A page acknowledges it as soon as it starts handling it, so the host
/// knows a page is alive and does not fall back to its own native question.</summary>
public sealed class CloseRequest
{
    private readonly TaskCompletionSource acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Acknowledge() => acknowledged.TrySetResult();
    public async Task<bool> WaitForAcknowledgementAsync(TimeSpan timeout) =>
        await Task.WhenAny(acknowledged.Task, Task.Delay(timeout)) == acknowledged.Task;
}

/// <summary>Web mode: the browser handles downloads, file inputs, and closing tabs.</summary>
public sealed class BrowserHostShell : IHostShell
{
    public bool IsDesktop => false;
    public Task<string?> PickSaveFileAsync(string suggestedName, FileFilter filter) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> PickFilesAsync(FileFilter filter, bool multiple) => throw new NotSupportedException();
    public Task<string?> PickFolderAsync() => throw new NotSupportedException();
    public event Func<CloseRequest, Task>? CloseRequested { add { } remove { } }
    public void CompleteClose() => throw new NotSupportedException();
}
