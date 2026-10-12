using System.Diagnostics;
namespace PereneTts.Desktop;

/// <summary>Messages before any window exists (already running, worker failed). Photino's message box needs a window,
/// so macOS uses osascript, with the text passed as arguments, never interpolated into the script.</summary>
public static class NativeAlert
{
    public static void Show(string title, string message, bool critical)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine($"{title}: {message}");
            return;
        }
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
        string[] arguments =
        [
            "-e", "on run argv",
            "-e", $"display alert (item 1 of argv) message (item 2 of argv){(critical ? " as critical" : "")} buttons {{\"OK\"}} default button \"OK\"",
            "-e", "end run",
            title, message,
        ];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            process?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine($"{title}: {message}"); }
    }
}
