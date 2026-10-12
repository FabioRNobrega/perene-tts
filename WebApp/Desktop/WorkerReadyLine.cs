using System.Text.RegularExpressions;
namespace PereneTts.Desktop;

/// <summary>Parses the worker's first stdout line, "PERENE_WORKER_READY port=&lt;n&gt;".</summary>
public static partial class WorkerReadyLine
{
    public static bool TryParse(string? line, out int port)
    {
        port = 0;
        if (line is null || ReadyLine().Match(line) is not { Success: true } match) return false;
        if (!int.TryParse(match.Groups["port"].Value, out var parsed) || parsed is < 1 or > 65535) return false;
        port = parsed;
        return true;
    }

    [GeneratedRegex("^PERENE_WORKER_READY port=(?<port>[0-9]{1,5})$")]
    private static partial Regex ReadyLine();
}
