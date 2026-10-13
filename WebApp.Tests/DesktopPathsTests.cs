using System.Collections;
using PereneTts.Desktop;

namespace WebApp.Tests;

public sealed class DesktopPathsTests : IDisposable
{
    private const string Bundle = "/Applications/Perene TTS.app/Contents/Resources/dotnet/";
    private readonly string temp = Directory.CreateTempSubdirectory("perene-paths-").FullName;

    [Fact]
    public void UserDirectoriesFollowMacOsConventions()
    {
        var paths = DesktopPaths.Resolve("/Users/ana", Bundle, new Hashtable());

        Assert.Equal("/Users/ana/Library/Application Support/Perene TTS", paths.Data);
        Assert.Equal("/Users/ana/Library/Application Support/Perene TTS/studio", paths.Studio);
        Assert.Equal("/Users/ana/Library/Application Support/Perene TTS/keys", paths.Keys);
        Assert.Equal("/Users/ana/Library/Application Support/Perene TTS/instance.lock", paths.InstanceLock);
        Assert.Equal("/Users/ana/Library/Logs/Perene TTS", paths.Logs);
        Assert.Equal("/Users/ana/Library/Logs/Perene TTS/worker.log", paths.WorkerLog);
        Assert.Equal("/Users/ana/Library/Caches/Perene TTS", paths.Cache);
    }

    [Fact]
    public void BundledResourcesComeFromContentsAndFfmpegFromMacOs()
    {
        var paths = DesktopPaths.Resolve("/Users/ana", Bundle, new Hashtable { ["PERENE_RESOURCES"] = "/ignored" });

        Assert.Equal("/Applications/Perene TTS.app/Contents/Resources/model", paths.Model);
        Assert.Equal("/Applications/Perene TTS.app/Contents/Resources/licenses", paths.Licenses);
        Assert.Equal("/Applications/Perene TTS.app/Contents/Resources/python/bin/python3.11", paths.Python);
        Assert.Equal("/Applications/Perene TTS.app/Contents/MacOS/ffmpeg", paths.Ffmpeg);
    }

    [Fact]
    public void UnbundledRunsUseThePereneResourcesVariable()
    {
        var paths = DesktopPaths.Resolve("/Users/ana", "/src/WebApp/bin/Debug/net10.0/", new Hashtable { ["PERENE_RESOURCES"] = "/opt/perene" });

        Assert.Equal("/opt/perene/model", paths.Model);
        Assert.Equal("/opt/perene/python/bin/python3.11", paths.Python);
        Assert.Equal("/opt/perene/ffmpeg", paths.Ffmpeg);
    }

    [Fact]
    public void UnbundledRunsWithoutResourcesFailClearly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DesktopPaths.Resolve("/Users/ana", "/src/WebApp/bin/Debug/net10.0/", new Hashtable()));
        Assert.Contains("PERENE_RESOURCES", error.Message);
    }

    [Fact]
    public void SavesStartInMusicWhenItExistsOtherwiseHome()
    {
        Assert.Equal(temp, DesktopPaths.Resolve(temp, Bundle, new Hashtable()).Music);
        Directory.CreateDirectory(Path.Combine(temp, "Music"));
        Assert.Equal(Path.Combine(temp, "Music"), DesktopPaths.Resolve(temp, Bundle, new Hashtable()).Music);
    }

    [Fact]
    public void WritableDirectoriesArePrivateToTheUser()
    {
        if (OperatingSystem.IsWindows()) return;
        var paths = DesktopPaths.Resolve(temp, Bundle, new Hashtable());

        paths.EnsureDirectories();

        foreach (var directory in new[] { paths.Data, paths.Studio, paths.Keys, paths.Logs, paths.Cache })
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Studio));
    }

    [Fact]
    public void InstanceLockAllowsOneHolderUntilReleased()
    {
        var path = Path.Combine(temp, "instance.lock");
        using (var first = InstanceLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(path));
        }
        using var again = InstanceLock.TryAcquire(path);
        Assert.NotNull(again);
    }

    [Fact]
    public void WorkerRunsIsolatedWithBytecodeInTheCacheAndNoSecretsInArguments()
    {
        var paths = DesktopPaths.Resolve("/Users/ana", Bundle, new Hashtable());

        var arguments = WorkerProcess.Arguments(paths);

        Assert.Equal(["-I", "-X", "pycache_prefix=/Users/ana/Library/Caches/Perene TTS/pycache", "-m", "app.desktop"], arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("secret", WorkerProcess.Settings(paths, "secret", "auto")["TTS_API_TOKEN"]);
        Assert.Equal(Path.Combine(paths.Resources, "pkuseg"), WorkerProcess.Settings(paths, "secret", "auto")["PKUSEG_HOME"]);
    }

    public void Dispose() => Directory.Delete(temp, recursive: true);
}
