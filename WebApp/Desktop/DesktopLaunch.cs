namespace PereneTts.Desktop;

/// <summary>Whether this process is the desktop app. Finder passes no arguments, so the bundle path is the trigger.</summary>
public static class DesktopLaunch
{
    public const string Argument = "--desktop";

    public static bool ShouldRun(string[] args, string baseDirectory) =>
        args.Contains(Argument, StringComparer.Ordinal) || BundleContents(baseDirectory) is not null;

    /// <summary>The bundle's Contents directory when baseDirectory is "…/&lt;name&gt;.app/Contents/MacOS/", otherwise null.</summary>
    public static string? BundleContents(string baseDirectory)
    {
        var macOs = baseDirectory.TrimEnd('/');
        if (!string.Equals(Path.GetFileName(macOs), "MacOS", StringComparison.Ordinal)) return null;
        var contents = Path.GetDirectoryName(macOs);
        if (contents is null || !string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal)) return null;
        var bundle = Path.GetDirectoryName(contents);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(bundle).Length > 4
            ? contents : null;
    }
}
