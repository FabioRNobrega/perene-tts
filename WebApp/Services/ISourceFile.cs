using Microsoft.AspNetCore.Components.Forms;
using PereneTts.Models;
namespace PereneTts.Services;

/// <summary>A user-chosen input file: a display name, a measured size, and a bounded read. Never a path for the worker.</summary>
public interface ISourceFile
{
    string Name { get; }
    long Size { get; }
    Stream Open(long maxBytes, CancellationToken cancellation);
}

/// <summary>Browser pick (web mode): the stream comes through the Blazor circuit.</summary>
public sealed class BrowserSourceFile(IBrowserFile file) : ISourceFile
{
    public string Name => file.Name;
    public long Size => file.Size;
    public Stream Open(long maxBytes, CancellationToken cancellation) => file.OpenReadStream(maxBytes, cancellation);
}

/// <summary>Native dialog pick (desktop mode): the path stays in the host; only the file name is shown or uploaded.</summary>
public sealed class LocalSourceFile : ISourceFile
{
    private readonly string path;

    public LocalSourceFile(string path)
    {
        this.path = path;
        Name = Path.GetFileName(path);
        Size = new FileInfo(path).Length;
    }

    public string Name { get; }
    public long Size { get; }
    // The size is checked again while reading, in case the file grew after it was measured.
    public Stream Open(long maxBytes, CancellationToken cancellation) => new BoundedReadStream(File.OpenRead(path), maxBytes);
}

/// <summary>Folder selection result: up to <see cref="BatchRules.MaxFiles"/> + 1 .txt files, plus how many other files were skipped.</summary>
public sealed record LocalSelection(IReadOnlyList<LocalSourceFile> Files, int Skipped, bool TooMany);

public static class LocalSourceFiles
{
    /// <summary>Matches the browser picker's limit on how many entries one selection may contain.</summary>
    public const int MaxEntries = 1000;

    /// <summary>Collects .txt files recursively like a browser folder picker, without following symlinked directories.
    /// macOS metadata (.DS_Store, AppleDouble ._*) counts as skipped, like any other non-.txt file.</summary>
    public static LocalSelection Enumerate(string folder)
    {
        var files = new List<LocalSourceFile>();
        int skipped = 0, seen = 0;
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(folder)]);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options).OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                if (entry is DirectoryInfo child)
                {
                    if (child.LinkTarget is null) pending.Push(child);
                    continue;
                }
                if (++seen > MaxEntries) return new(files, skipped, TooMany: true);
                if (IsMacMetadata(entry.Name) || !BatchRules.IsTextFile(entry.Name)) { skipped++; continue; }
                files.Add(new LocalSourceFile(entry.FullName));
                if (files.Count > BatchRules.MaxFiles) return new(files, skipped, TooMany: false);
            }
        }
        return new(files, skipped, TooMany: false);
    }

    public static bool IsMacMetadata(string name) => name == ".DS_Store" || name.StartsWith("._", StringComparison.Ordinal);
}

/// <summary>Read-only stream that throws <see cref="IOException"/> once more than maxBytes have been read.</summary>
public sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
{
    private long total;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => total; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));
    private int Count(int read)
    {
        total += read;
        if (total > maxBytes) throw new IOException($"File exceeds {maxBytes} bytes.");
        return read;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
