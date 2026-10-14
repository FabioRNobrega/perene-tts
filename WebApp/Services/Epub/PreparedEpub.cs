namespace PereneTts.Services.Epub;

/// <summary>The archive the parser reads; disposing deletes the rebuilt temporary file when a repair ran.</summary>
public sealed class PreparedEpub : IAsyncDisposable
{
    private readonly bool ownsStream;

    internal PreparedEpub(Stream stream, bool repaired)
    {
        Stream = stream;
        Repaired = repaired;
        ownsStream = repaired;
    }

    public Stream Stream { get; }
    public bool Repaired { get; }

    public ValueTask DisposeAsync() => ownsStream ? Stream.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>Private, randomly named temporary files that the OS deletes when the stream closes.</summary>
internal static class EpubTempFile
{
    public static FileStream Create() => new(
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.DeleteOnClose);
}
