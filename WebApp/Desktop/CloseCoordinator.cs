using PereneTts.Services;
namespace PereneTts.Desktop;

/// <summary>Decides when the window may close. A close or quit is first refused, then the open studio page asks the
/// user (it acknowledges within 2 seconds); without a live page the host asks natively. CompleteClose lets it through.</summary>
public sealed class CloseCoordinator(Func<Task> hostFallback, Action closeWindow, Action shutdown)
{
    private static readonly TimeSpan AcknowledgeTimeout = TimeSpan.FromSeconds(2);
    private int allowClose;
    private int requesting;

    public event Func<CloseRequest, Task>? CloseRequested;
    private bool CloseAllowed => Volatile.Read(ref allowClose) == 1;

    /// <summary>AppKit windowShouldClose: (red button, ⌘W).</summary>
    public bool WindowShouldClose()
    {
        if (CloseAllowed) return true;
        Request();
        return false;
    }

    /// <summary>AppKit applicationShouldTerminate: (⌘Q, Dock → Quit, last window closed, log out).</summary>
    public bool ShouldTerminate(bool systemQuit)
    {
        if (!CloseAllowed && !systemQuit)
        {
            Request();
            return false;
        }
        // AppKit exits the process right after this returns, so the host stops the web server and worker now.
        shutdown();
        return true;
    }

    /// <summary>Photino's closing handler. On macOS it runs from windowWillClose:, where the close can no longer
    /// be cancelled, so it only shuts down; the hooks above do the confirming.</summary>
    public bool OnWindowClosing(object sender, EventArgs args)
    {
        shutdown();
        return false;
    }

    public void CompleteClose()
    {
        Interlocked.Exchange(ref allowClose, 1);
        // Off the caller's thread: the circuit must not block while the main thread shuts the web host down.
        _ = Task.Run(closeWindow);
    }

    private void Request()
    {
        if (Interlocked.Exchange(ref requesting, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await RequestAsync(); }
            finally { Volatile.Write(ref requesting, 0); }
        });
    }

    private async Task RequestAsync()
    {
        var request = new CloseRequest();
        foreach (var handler in CloseRequested?.GetInvocationList().Cast<Func<CloseRequest, Task>>() ?? [])
            _ = Observe(handler, request);
        if (await request.WaitForAcknowledgementAsync(AcknowledgeTimeout)) return;
        await hostFallback();
    }

    // A page that fails after acknowledging leaves the window open; the user can close again.
    private static async Task Observe(Func<CloseRequest, Task> handler, CloseRequest request)
    {
        try { await handler(request); }
        catch (Exception) { }
    }
}
