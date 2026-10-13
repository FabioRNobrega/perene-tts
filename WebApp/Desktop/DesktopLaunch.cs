namespace PereneTts.Desktop;

/// <summary>Whether this process is the desktop app. Finder passes no arguments, so the bundle path is the trigger.</summary>
public static class DesktopLaunch
{
    public const string Argument = "--desktop";

    public static bool ShouldRun(string[] args, string baseDirectory) =>
        args.Contains(Argument, StringComparer.Ordinal) || BundleContents(baseDirectory) is not null;

    /// <summary>The bundle's Contents directory when baseDirectory is "…/&lt;name&gt;.app/Contents/Resources/dotnet/",
    /// otherwise null. The launcher in Contents/MacOS finds WebApp.dll through a symlink, and the .NET host makes the
    /// folder the link points to the app's base directory (packaging/macos/build.sh, bundle).</summary>
    public static string? BundleContents(string baseDirectory)
    {
        var dotnet = baseDirectory.TrimEnd('/');
        if (!string.Equals(Path.GetFileName(dotnet), "dotnet", StringComparison.Ordinal)) return null;
        var resources = Path.GetDirectoryName(dotnet);
        if (resources is null || !string.Equals(Path.GetFileName(resources), "Resources", StringComparison.Ordinal)) return null;
        var contents = Path.GetDirectoryName(resources);
        if (contents is null || !string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal)) return null;
        var bundle = Path.GetDirectoryName(contents);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(bundle).Length > 4
            ? contents : null;
    }
}
