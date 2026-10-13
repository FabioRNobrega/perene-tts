using System.Collections;
namespace PereneTts.Desktop;

/// <summary>Where the desktop app reads its bundled resources and writes per-user state. The bundle is never written.</summary>
public sealed record DesktopPaths(
    string Data, string Studio, string Keys, string InstanceLock, string Logs, string Cache, string Music,
    string Resources, string Model, string Licenses, string Python, string Ffmpeg)
{
    public const string AppName = "Perene TTS";
    public const string ResourcesVariable = "PERENE_RESOURCES";
    /// <summary>The bundled standalone Python runs the worker as <c>python3.11 -m app.desktop</c>.</summary>
    public const string PythonExecutable = "python3.11";
    public string WorkerLog => Path.Combine(Logs, "worker.log");

    /// <summary>macOS per-user locations under home; resources come from the bundle, or from PERENE_RESOURCES
    /// (an unpacked layout with model/, licenses/, python/ and ffmpeg) for unbundled development runs.
    /// Python, its packages and the .NET publish output live in Contents/Resources: Contents/MacOS holds only Mach-O
    /// files (the launcher and ffmpeg), because anything else there is treated as nested code when signing.</summary>
    public static DesktopPaths Resolve(string home, string baseDirectory, IDictionary environment)
    {
        var data = Path.Combine(home, "Library", "Application Support", AppName);
        var music = Path.Combine(home, "Music");
        string resources, executables;
        if (DesktopLaunch.BundleContents(baseDirectory) is { } contents)
        {
            resources = Path.Combine(contents, "Resources");
            executables = Path.Combine(contents, "MacOS");
        }
        else if (environment[ResourcesVariable] is string unpacked && unpacked.Length > 0)
        {
            resources = executables = Path.GetFullPath(unpacked);
        }
        else
        {
            throw new InvalidOperationException(
                $"Perene TTS is not running from its app bundle. Set {ResourcesVariable} to a folder with model/, python/ and ffmpeg.");
        }
        return new(
            Data: data,
            Studio: Path.Combine(data, "studio"),
            Keys: Path.Combine(data, "keys"),
            InstanceLock: Path.Combine(data, "instance.lock"),
            Logs: Path.Combine(home, "Library", "Logs", AppName),
            Cache: Path.Combine(home, "Library", "Caches", AppName),
            Music: Directory.Exists(music) ? music : home,
            Resources: resources,
            Model: Path.Combine(resources, "model"),
            Licenses: Path.Combine(resources, "licenses"),
            Python: Path.Combine(resources, "python", "bin", PythonExecutable),
            Ffmpeg: Path.Combine(executables, "ffmpeg"));
    }

    /// <summary>Creates the writable directories readable only by this user (0700).</summary>
    public void EnsureDirectories()
    {
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        foreach (var directory in new[] { Data, Studio, Keys, Logs, Cache })
        {
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); continue; }
            Directory.CreateDirectory(directory, privateMode);
            File.SetUnixFileMode(directory, privateMode);
        }
    }
}
