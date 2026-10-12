namespace PereneTts.Desktop;

/// <summary>Exclusive lock on instance.lock for the process lifetime. On macOS .NET takes an advisory flock,
/// which the kernel releases when the process ends, even after a crash.</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream stream;
    private InstanceLock(FileStream stream) => this.stream = stream;

    /// <summary>Null when another instance holds the lock.</summary>
    public static InstanceLock? TryAcquire(string path)
    {
        try { return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException) { return null; }
    }

    public void Dispose() => stream.Dispose();
}
