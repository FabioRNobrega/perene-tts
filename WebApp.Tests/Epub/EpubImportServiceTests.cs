using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PereneTts.Services.Epub;

namespace WebApp.Tests.Epub;

[Collection(TempFilesCollection.Name)]
public sealed class EpubImportServiceTests
{
    private const string ClientName = "Private Client Name.epub";
    private readonly RecordingLogger _logger = new();

    private EpubImportService Service()
    {
        var options = Options.Create(new EpubParserOptions());
        return new EpubImportService(new EpubArchiveRepairer(options), new EpubChapterParser(options), options, _logger);
    }

    [Fact]
    public async Task OversizedFileMapsToTheSizeMessage()
    {
        var before = TempFiles.Snapshot();
        var file = new FakeBrowserFile(new SyntheticEpubBuilder().BuildBytes(), size: 100L * 1024 * 1024 + 1);

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => Service().ImportAsync(file, CancellationToken.None));

        Assert.Equal(EpubParseErrorKind.InvalidRequest, exception.Kind);
        Assert.Equal("The EPUB exceeds 100 MiB.", exception.PublicMessage);
        Assert.Equal(100L * 1024 * 1024, file.RequestedMaxSize);
        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task ConformingEpubImportsWithoutRepair()
    {
        var before = TempFiles.Snapshot();

        var result = await Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().BuildBytes()), CancellationToken.None);

        Assert.False(result.Repaired);
        Assert.Equal("Á Sample Book", result.Book.Title);
        Assert.Equal([1, 2, 3], result.Book.Chapters.Select(chapter => chapter.Number));
        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task ReorderedMimetypeImportsWithRepair()
    {
        var before = TempFiles.Snapshot();
        var bytes = new SyntheticEpubBuilder().WithMimetypeAfterContainer().BuildBytes();

        var result = await Service().ImportAsync(new FakeBrowserFile(bytes), CancellationToken.None);

        Assert.True(result.Repaired);
        Assert.Equal(3, result.Book.Chapters.Count);
        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task InvalidEpubFailsAndLeavesNoTemporaryFile()
    {
        var before = TempFiles.Snapshot();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() =>
            Service().ImportAsync(new FakeBrowserFile("not an epub"u8.ToArray()), CancellationToken.None));

        Assert.Equal("The source is not a valid ZIP archive.", exception.PublicMessage);
        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task CancellationPropagatesAndLeavesNoTemporaryFile()
    {
        var before = TempFiles.Snapshot();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().BuildBytes()), new CancellationToken(canceled: true)));

        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task LogsCarryCountsButNoTitleTextOrFileName()
    {
        await Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().BuildBytes()), CancellationToken.None);
        await Assert.ThrowsAsync<EpubParseException>(() =>
            Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().WithEncryption().BuildBytes()), CancellationToken.None));

        Assert.Equal(2, _logger.Messages.Count);
        Assert.Contains("3 chapters", _logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains("UnsupportedContent", _logger.Messages[1], StringComparison.Ordinal);
        Assert.All(_logger.Messages, message =>
        {
            Assert.DoesNotContain("Sample Book", message, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ClientName, message, StringComparison.Ordinal);
        });
    }

    private sealed class FakeBrowserFile(byte[] data, long? size = null) : IBrowserFile
    {
        public long? RequestedMaxSize { get; private set; }
        public string Name => ClientName;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size { get; } = size ?? data.Length;
        public string ContentType => "application/epub+zip";

        // Mirrors BrowserFile: a file larger than maxAllowedSize fails with IOException.
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            RequestedMaxSize = maxAllowedSize;
            if (Size > maxAllowedSize) throw new IOException($"Supplied file with size {Size} bytes exceeds the maximum of {maxAllowedSize} bytes.");
            return new MemoryStream(data, writable: false);
        }
    }

    private sealed class RecordingLogger : ILogger<EpubImportService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
