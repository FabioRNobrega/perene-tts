using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using PereneTts.Services.Epub;

namespace WebApp.Tests.Epub;

[Collection(TempFilesCollection.Name)]
public sealed class EpubArchiveRepairerTests
{
    public enum MarkerDefect { Missing, Reordered, WrongValue, CompressedNotFirst }

    private static EpubArchiveRepairer Repairer(EpubParserOptions? options = null) => new(Options.Create(options ?? new EpubParserOptions()));

    [Fact]
    public async Task ConformingArchiveIsReturnedUnchanged()
    {
        var before = TempFiles.Snapshot();
        using var source = new SyntheticEpubBuilder().Build();

        await using var prepared = await Repairer().PrepareAsync(source, CancellationToken.None);

        Assert.False(prepared.Repaired);
        Assert.Same(source, prepared.Stream);
        Assert.Equal(0, source.Position);
        TempFiles.AssertNoneAdded(before);
    }

    [Theory]
    [InlineData(MarkerDefect.Missing)]
    [InlineData(MarkerDefect.Reordered)]
    [InlineData(MarkerDefect.WrongValue)]
    [InlineData(MarkerDefect.CompressedNotFirst)]
    public async Task NonConformingMarkerIsRebuiltAndThenParses(MarkerDefect defect)
    {
        using var source = Defective(defect);
        var originalEntries = FileEntries(source);

        await using var prepared = await Repairer().PrepareAsync(source, CancellationToken.None);

        Assert.True(prepared.Repaired);
        Assert.NotSame(source, prepared.Stream);
        using (var rebuilt = new ZipArchive(prepared.Stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal("mimetype", rebuilt.Entries[0].FullName);
            Assert.Single(rebuilt.Entries, entry => entry.FullName == "mimetype");
            Assert.Equal("application/epub+zip", Read(rebuilt.Entries[0]));
            Assert.Equal(rebuilt.Entries[0].Length, rebuilt.Entries[0].CompressedLength);
        }
        Assert.Equal(originalEntries, FileEntries(prepared.Stream));

        var book = await new EpubChapterParser(Options.Create(new EpubParserOptions())).ParseAsync(prepared.Stream);
        var twin = await new EpubChapterParser(Options.Create(new EpubParserOptions())).ParseAsync(new SyntheticEpubBuilder().Build());
        Assert.Equal(twin.Chapters.Select(chapter => chapter.Paragraphs), book.Chapters.Select(chapter => chapter.Paragraphs));
    }

    [Theory]
    [InlineData("/OPS/abs.xhtml", "The archive contains an unsafe entry path.")]
    [InlineData("OPS/../../escape.xhtml", "The archive contains an unsafe entry path.")]
    [InlineData("..", "The archive contains an unsafe entry path.")]
    [InlineData("OPS\\text.xhtml", "The archive contains an unsafe entry path.")]
    [InlineData("C:/text.xhtml", "The archive contains an unsafe entry path.")]
    [InlineData("OPS/package.opf", "The archive contains duplicate entry paths.")]
    [InlineData("mimetype/", "The archive contains an invalid mimetype directory.")]
    public async Task UnsafeNonConformingArchivesAreRejected(string extraEntry, string message)
    {
        using var source = Zip(("OPS/package.opf", "<package />", 0), (extraEntry, "x", 0));

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => Repairer().PrepareAsync(source, CancellationToken.None));

        Assert.Equal(EpubParseErrorKind.InvalidEpub, exception.Kind);
        Assert.Equal(message, exception.PublicMessage);
    }

    [Fact]
    public async Task SymbolicLinkEntriesAreRejected()
    {
        using var source = Zip(("OPS/package.opf", "<package />", 0), ("OPS/link", "/etc/passwd", unchecked((int)(0xA1FFu << 16))));

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => Repairer().PrepareAsync(source, CancellationToken.None));

        Assert.Equal("The archive contains symbolic links.", exception.PublicMessage);
    }

    [Theory]
    [InlineData(2, 1000, 1000, "The EPUB exceeds the configured archive entry limit.")]
    [InlineData(10, 5, 1000, "An EPUB resource exceeds the configured entry size limit.")]
    [InlineData(10, 1000, 12, "The EPUB exceeds the configured uncompressed size limit.")]
    public async Task ArchiveLimitsApplyBeforeRebuilding(int entries, long entryBytes, long totalBytes, string message)
    {
        var before = TempFiles.Snapshot();
        using var source = Zip(("a.txt", "123456", 0), ("b.txt", "123456", 0), ("c.txt", "123456", 0));
        var options = new EpubParserOptions { MaxArchiveEntryCount = entries, MaxEntryBytes = entryBytes, MaxTotalUncompressedBytes = totalBytes };

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => Repairer(options).PrepareAsync(source, CancellationToken.None));

        Assert.Equal(message, exception.PublicMessage);
        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task NonZipSourceIsRejected()
    {
        using var source = new MemoryStream("plain text, not a zip"u8.ToArray());

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => Repairer().PrepareAsync(source, CancellationToken.None));

        Assert.Equal("The source is not a valid ZIP archive.", exception.PublicMessage);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var before = TempFiles.Snapshot();
        using var source = new SyntheticEpubBuilder().WithMimetypeAfterContainer().Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repairer().PrepareAsync(source, new CancellationToken(canceled: true)));

        TempFiles.AssertNoneAdded(before);
    }

    [Fact]
    public async Task DisposingTheRepairDeletesTheRebuiltFile()
    {
        var before = TempFiles.Snapshot();
        using var source = new SyntheticEpubBuilder().WithoutMimetype().Build();

        var prepared = await Repairer().PrepareAsync(source, CancellationToken.None);
        var path = Assert.IsType<FileStream>(prepared.Stream).Name;
        Assert.True(File.Exists(path));
        await prepared.DisposeAsync();

        Assert.False(File.Exists(path));
        TempFiles.AssertNoneAdded(before);
    }

    private static MemoryStream Defective(MarkerDefect defect) => defect switch
    {
        MarkerDefect.Missing => new SyntheticEpubBuilder().WithoutMimetype().Build(),
        MarkerDefect.Reordered => new SyntheticEpubBuilder().WithMimetypeAfterContainer().Build(),
        MarkerDefect.WrongValue => new SyntheticEpubBuilder().WithWrongMimetype().Build(),
        _ => CompressedMarkerLast()
    };

    // Copies a conforming book, then appends a deflated mimetype marker at the end.
    private static MemoryStream CompressedMarkerLast()
    {
        using var conforming = new ZipArchive(new SyntheticEpubBuilder().Build(), ZipArchiveMode.Read);
        var entries = conforming.Entries.Where(entry => entry.FullName != "mimetype")
            .Select(entry => (entry.FullName, Read(entry), 0)).ToList();
        entries.Add(("mimetype", "application/epub+zip", 0));
        return Zip(CompressionLevel.Optimal, [.. entries]);
    }

    private static MemoryStream Zip(params (string Name, string Content, int Attributes)[] entries) =>
        Zip(CompressionLevel.Optimal, entries);

    private static MemoryStream Zip(CompressionLevel level, params (string Name, string Content, int Attributes)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, attributes) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                if (attributes != 0) entry.ExternalAttributes = attributes;
                if (name.EndsWith('/')) continue;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static List<(string Name, string Content)> FileEntries(Stream stream)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(entry => entry.FullName != "mimetype" && !entry.FullName.EndsWith('/'))
            .Select(entry => (entry.FullName, Read(entry))).ToList();
        stream.Position = 0;
        return entries;
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
