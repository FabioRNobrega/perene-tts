using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PereneTts.Models;
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
        Assert.Null(result.Fallback);
        Assert.Equal("Á Sample Book", result.Book!.Title);
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
        Assert.Equal(3, result.Book!.Chapters.Count);
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
    public async Task UnsupportedChapterStructureReturnsAPartsOffer()
    {
        var before = TempFiles.Snapshot();

        var result = await Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().AsPageListEpub2().BuildBytes()), CancellationToken.None);

        Assert.Null(result.Book);
        var offer = Assert.IsType<EpubFallbackOffer>(result.Fallback);
        Assert.Equal("An EPUB 2 NCX chapter target does not match its numeric heading.", offer.Reason);
        Assert.Equal(BookStructure.Parts, offer.Parts.Structure);
        Assert.Equal("eng", offer.Parts.Language);
        var part = Assert.Single(offer.Parts.Chapters);
        Assert.Equal((1, 1, BookItemKind.Part), (part.Number, part.Order, part.Kind));
        Assert.Equal(["SYNTHETIC KNIGHT", "Synthetic knight prose one.", "Synthetic knight prose two."], part.Paragraphs);
        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task OfferUsesTheConfiguredPartSize()
    {
        // 70 characters at a 45-character target: two parts, split at a paragraph boundary.
        var options = Options.Create(new EpubParserOptions { FallbackPartCharacters = 45 });
        var service = new EpubImportService(new EpubArchiveRepairer(options), new EpubChapterParser(options), options, _logger);

        var result = await service.ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().AsPageListEpub2().BuildBytes()), CancellationToken.None);

        Assert.Equal([1, 2], result.Fallback!.Parts.Chapters.Select(part => part.Order));
        Assert.Equal(["Synthetic knight prose two."], result.Fallback.Parts.Chapters[1].Paragraphs);
    }

    [Fact]
    public async Task OtherFailuresNeverOfferParts()
    {
        var encrypted = await Assert.ThrowsAsync<EpubParseException>(() =>
            Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().AsPageListEpub2().WithEncryption().BuildBytes()), CancellationToken.None));

        Assert.Equal(EpubParseErrorKind.UnsupportedContent, encrypted.Kind);
    }

    [Fact]
    public async Task BookWithoutReadableTextFailsWithoutOffer()
    {
        var before = TempFiles.Snapshot();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() =>
            Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().AsPageListEpub2(withText: false).BuildBytes()), CancellationToken.None));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
        Assert.EndsWith("This book has no readable text.", exception.PublicMessage, StringComparison.Ordinal);
        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task LogsCarryCountsButNoTitleTextOrFileName()
    {
        await Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().BuildBytes()), CancellationToken.None);
        await Assert.ThrowsAsync<EpubParseException>(() =>
            Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().WithEncryption().BuildBytes()), CancellationToken.None));

        await Service().ImportAsync(new FakeBrowserFile(new SyntheticEpubBuilder().AsPageListEpub2().BuildBytes()), CancellationToken.None);

        Assert.Equal(3, _logger.Messages.Count);
        Assert.Contains("chapters, 3 chapters, 0 sections, 0 parts", _logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains("UnsupportedContent", _logger.Messages[1], StringComparison.Ordinal);
        Assert.Contains("fallback offer, 0 chapters, 0 sections, 1 parts", _logger.Messages[2], StringComparison.Ordinal);
        Assert.All(_logger.Messages, message =>
        {
            Assert.DoesNotContain("Sample", message, StringComparison.Ordinal);
            Assert.DoesNotContain("KNIGHT", message, StringComparison.OrdinalIgnoreCase);
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
