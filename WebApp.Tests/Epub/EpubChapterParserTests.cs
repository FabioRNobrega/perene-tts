using Microsoft.Extensions.Options;
using PereneTts.Models;
using PereneTts.Services.Epub;

// Ported from book-notes-ia/services/EbookParseService.Tests/EpubChapterParserTests.cs; input-folder and symlink tests no longer apply.
namespace WebApp.Tests.Epub;

public sealed class EpubChapterParserTests
{
    [Fact]
    public async Task ParseAsync_UsesNavigationFragmentsAndKeepsChaptersIsolated()
    {
        using var epub = new SyntheticEpubBuilder().Build();
        var parser = CreateParser();

        var result = await parser.ParseAsync(epub);

        Assert.Equal("Á Sample Book", result.Title);
        Assert.Equal("en-US", result.Language);
        Assert.Equal(3, result.Chapters.Count);
        Assert.Equal([1, 2, 3], result.Chapters.Select(chapter => chapter.Number));
        Assert.Contains("First synthetic paragraph — safe.", result.Chapters[0].Paragraphs);
        Assert.Contains("Second paragraph.", result.Chapters[0].Paragraphs);
        Assert.DoesNotContain(result.Chapters[0].Paragraphs, text => text.Contains("chapter two", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Chapters.SelectMany(chapter => chapter.Paragraphs), text =>
            text.Contains("PART ONE", StringComparison.Ordinal)
            || text.Contains("private script", StringComparison.Ordinal)
            || text.Contains("navigation noise", StringComparison.Ordinal)
            || text.Contains("image words", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_AcceptsExplicitSemanticChapterWithNonNumericLabel()
    {
        var navigation = """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body><nav epub:type="toc"><ol><li><a href="../text/book.xhtml#opening">Opening</a></li></ol></nav></body>
            </html>
            """;
        var content = """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body><section id="opening" epub:type="chapter"><h2>Opening</h2><p>Semantic chapter prose.</p></section></body>
            </html>
            """;
        using var epub = new SyntheticEpubBuilder().WithNavigation(navigation).WithContent(content)
            .Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Single(result.Chapters);
        Assert.Equal("Semantic chapter prose.", Assert.Single(result.Chapters[0].Paragraphs));
    }

    [Fact]
    public async Task ParseAsync_UsesEpub2NcxWholeDocumentTargets()
    {
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal("NCX Sample Book", result.Title);
        Assert.Equal("en", result.Language);
        Assert.Equal([1, 2], result.Chapters.Select(chapter => chapter.Number));
        Assert.Equal(
            ["First NCX chapter paragraph.", "More chapter one."],
            result.Chapters[0].Paragraphs);
        Assert.Equal("Only chapter two NCX prose.", Assert.Single(result.Chapters[1].Paragraphs));
        Assert.DoesNotContain(
            result.Chapters.SelectMany(chapter => chapter.Paragraphs),
            paragraph => paragraph.Contains("Preface", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_UsesEpub2NcxFragmentTarget()
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx.Replace(
            "src=\"text/chapter1.xhtml\"",
            "src=\"text/chapter1.xhtml#one\"",
            StringComparison.Ordinal);
        var chapter = """
            <html xmlns="http://www.w3.org/1999/xhtml">
              <body><section id="one"><h2>1</h2><p>Fragment NCX prose.</p></section></body>
            </html>
            """;
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx()
            .WithResource("OPS/toc.ncx", ncx)
            .WithResource("OPS/text/chapter1.xhtml", chapter)
            .Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal("Fragment NCX prose.", Assert.Single(result.Chapters[0].Paragraphs));
    }

    [Fact]
    public async Task ParseAsync_UsesEpub2NcxNumberedTitleLabelsAndHeadings()
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx
            .Replace("<text>Chapter 1</text>", "<text>1. Start</text>", StringComparison.Ordinal)
            .Replace("<text>Chapter 2</text>", "<text>2. Define</text>", StringComparison.Ordinal);
        var chapterOne = SyntheticEpubBuilder.Epub2ChapterOne.Replace("<h2>1</h2>", "<h2>1START</h2>", StringComparison.Ordinal);
        var chapterTwo = SyntheticEpubBuilder.Epub2ChapterTwo.Replace("<h2>2</h2>", "<h2>2 DEFINE</h2>", StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx()
            .WithResource("OPS/toc.ncx", ncx)
            .WithResource("OPS/text/chapter1.xhtml", chapterOne)
            .WithResource("OPS/text/chapter2.xhtml", chapterTwo)
            .Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal([1, 2], result.Chapters.Select(chapter => chapter.Number));
        Assert.Equal("First NCX chapter paragraph.", result.Chapters[0].Paragraphs[0]);
        Assert.Equal("Only chapter two NCX prose.", Assert.Single(result.Chapters[1].Paragraphs));
    }

    [Fact]
    public async Task ParseAsync_UsesEpub2NcxNumberedTitleFragmentTarget()
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx.Replace(
            "<text>Chapter 1</text>",
            "<text>1. Start</text>",
            StringComparison.Ordinal).Replace(
            "src=\"text/chapter1.xhtml\"",
            "src=\"text/chapter1.xhtml#one\"",
            StringComparison.Ordinal);
        var chapter = """
            <html xmlns="http://www.w3.org/1999/xhtml">
              <body><section id="one"><h2>1 START</h2><p>Fragment NCX prose.</p></section></body>
            </html>
            """;
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx()
            .WithResource("OPS/toc.ncx", ncx)
            .WithResource("OPS/text/chapter1.xhtml", chapter)
            .Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal("Fragment NCX prose.", Assert.Single(result.Chapters[0].Paragraphs));
    }

    [Fact]
    public async Task ParseAsync_UsesSpineResourcesFollowingWholeDocumentNcxTitleTarget()
    {
        const string package = """
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">test</dc:identifier><dc:title>Spine test</dc:title><dc:language>en</dc:language></metadata>
              <manifest>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
                <item id="title" href="text/title.xhtml" media-type="application/xhtml+xml" />
                <item id="body" href="text/body.xhtml" media-type="application/xhtml+xml" />
                <item id="next" href="text/next.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine toc="ncx"><itemref idref="title" /><itemref idref="body" /><itemref idref="next" /></spine>
            </package>
            """;
        const string ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
              <navPoint id="one"><navLabel><text>1. Start</text></navLabel><content src="text/title.xhtml" /></navPoint>
              <navPoint id="two"><navLabel><text>2. Next</text></navLabel><content src="text/next.xhtml" /></navPoint>
            </navMap></ncx>
            """;
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx()
            .WithPackage(package)
            .WithResource("OPS/toc.ncx", ncx)
            .WithResource("OPS/text/title.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>1START</h1></body></html>")
            .WithResource("OPS/text/body.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Spine continuation prose.</p></body></html>")
            .WithResource("OPS/text/next.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>2NEXT</h1><p>Second chapter prose.</p></body></html>")
            .Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal("Spine continuation prose.", Assert.Single(result.Chapters[0].Paragraphs));
        Assert.Equal("Second chapter prose.", Assert.Single(result.Chapters[1].Paragraphs));
    }

    [Fact]
    public async Task ParseAsync_RejectsEpub2NcxNumberedTitleWithMismatchedHeading()
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx.Replace(
            "<text>Chapter 1</text>",
            "<text>1. Start</text>",
            StringComparison.Ordinal);
        var chapter = SyntheticEpubBuilder.Epub2ChapterOne.Replace("<h2>1</h2>", "<h2>9 START</h2>", StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx()
            .WithResource("OPS/toc.ncx", ncx)
            .WithResource("OPS/text/chapter1.xhtml", chapter)
            .Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(
            () => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_NormalizesXhtmlNbspWithoutResolvingExternalDtd()
    {
        var chapter = SyntheticEpubBuilder.Epub2ChapterOne.Replace(
            "First NCX",
            "First&nbsp;NCX",
            StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithResource("OPS/text/chapter1.xhtml", chapter)
            .Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Contains("First NCX chapter paragraph.", result.Chapters[0].Paragraphs);
    }

    [Fact]
    public async Task ParseAsync_RejectsEpub2PackageWithoutSpineNcxReference()
    {
        var package = SyntheticEpubBuilder.Epub2Package.Replace(" toc=\"ncx\"", string.Empty, StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithPackage(package)
            .Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(
            () => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_RejectsEpub2NcxWithoutNavMap()
    {
        const string ncx = "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\" />";
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithResource("OPS/toc.ncx", ncx)
            .Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(
            () => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_RejectsEpub2NcxChapterWithMismatchedHeading()
    {
        var chapter = SyntheticEpubBuilder.Epub2ChapterOne.Replace("<h2>1</h2>", "<h2>9</h2>", StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithResource("OPS/text/chapter1.xhtml", chapter)
            .Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(
            () => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_RejectsEpub2NcxUndeclaredChapterTarget()
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx.Replace(
            "src=\"text/chapter1.xhtml\"",
            "src=\"text/missing.xhtml\"",
            StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithResource("OPS/toc.ncx", ncx)
            .Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(
            () => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_RejectsDuplicateEpub2NcxChapterTarget()
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx
            .Replace("<text>Chapter 2</text>", "<text>Chapter 1</text>", StringComparison.Ordinal)
            .Replace("src=\"text/chapter2.xhtml\"", "src=\"text/chapter1.xhtml\"", StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithResource("OPS/toc.ncx", ncx)
            .Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(
            () => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
        Assert.Contains("duplicate chapter target", exception.PublicMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_InfersLanguageForNumericEpub2NcxChapters()
    {
        using var epub = BuildNumericNcxWithoutPackageLanguage("pt-br", "pt-br");

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal("pt-br", result.Language);
        Assert.Equal([1, 2], result.Chapters.Select(chapter => chapter.Number));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("pt-br", null)]
    [InlineData("pt-br", "en")]
    public async Task ParseAsync_RejectsIncompleteOrInconsistentInferredNcxLanguage(
        string? chapterOneLanguage,
        string? chapterTwoLanguage)
    {
        using var epub = BuildNumericNcxWithoutPackageLanguage(
            chapterOneLanguage,
            chapterTwoLanguage);

        var exception = await Assert.ThrowsAsync<EpubParseException>(
            () => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.InvalidEpub, exception.Kind);
        Assert.Contains("language", exception.PublicMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_RejectsEpub3WithoutPackageLanguage()
    {
        var package = RemovePackageLanguage(SyntheticEpubBuilder.DefaultPackage);
        using var epub = new SyntheticEpubBuilder().WithPackage(package).Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(
            () => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.InvalidEpub, exception.Kind);
        Assert.Contains("language", exception.PublicMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParseAsync_RejectsWrongOrReorderedMimetype(bool wrongValue)
    {
        var builder = wrongValue
            ? new SyntheticEpubBuilder().WithWrongMimetype()
            : new SyntheticEpubBuilder().WithMimetypeAfterContainer();
        using var epub = builder.Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.InvalidEpub, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_RejectsEncryptedPublication()
    {
        using var epub = new SyntheticEpubBuilder().WithEncryption().Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedContent, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_RejectsDuplicateChapterNumber()
    {
        var navigation = SyntheticEpubBuilder.DefaultNavigation.Replace(
            "../text/book.xhtml#two\">2</a>",
            "../text/book.xhtml#one\">1</a>",
            StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().WithNavigation(navigation).Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_EnforcesConfiguredEntryLimit()
    {
        using var epub = new SyntheticEpubBuilder().Build();
        var parser = new EpubChapterParser(Options.Create(new EpubParserOptions { MaxArchiveEntryCount = 2 }));

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => parser.ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.InvalidEpub, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_EnforcesMaxBookCharacters()
    {
        using var epub = new SyntheticEpubBuilder().Build();
        var parser = new EpubChapterParser(Options.Create(new EpubParserOptions { MaxBookCharacters = 20 }));

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => parser.ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedContent, exception.Kind);
        Assert.Contains("20 characters", exception.PublicMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_MapsNonZipStreamToInvalidEpub()
    {
        using var epub = new MemoryStream("not a zip archive"u8.ToArray());

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.InvalidEpub, exception.Kind);
        Assert.Equal("The file is not a valid EPUB archive.", exception.PublicMessage);
    }

    [Fact]
    public async Task ParseAsync_MapsNonSeekableStreamToInvalidEpub()
    {
        using var epub = new NonSeekableStream(new SyntheticEpubBuilder().BuildBytes());

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.InvalidEpub, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_RejectsMissingMimetypeWithoutRepair()
    {
        using var epub = new SyntheticEpubBuilder().WithoutMimetype().Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal("The archive does not have a conforming EPUB media-type marker.", exception.PublicMessage);
    }

    [Fact]
    public async Task ParseAsync_LeavesTheStreamOpen()
    {
        using var epub = new SyntheticEpubBuilder().Build();

        await CreateParser().ParseAsync(epub);

        Assert.True(epub.CanRead);
    }

    // --- Specs/20261014112604-epub-chapter-discovery-and-fallback ---

    [Theory]
    [InlineData("Chapter 1: The Start")]
    [InlineData("chapter 1 – Start")]
    [InlineData("CHAPTER 1. Start")]
    [InlineData("Capítulo 1: O Fim")]
    [InlineData("Kapitel 1")]
    public async Task ParseAsync_AcceptsChapterWordLabelsWithOptionalTitles(string label)
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx.Replace("<text>Chapter 1</text>", $"<text>{label}</text>", StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithResource("OPS/toc.ncx", ncx).Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal([1, 2], result.Chapters.Select(chapter => chapter.Number));
    }

    [Theory]
    [InlineData("Chapter one")]
    [InlineData("Chapters 1-3")]
    public async Task ParseAsync_IgnoresLabelsWithoutAChapterNumber(string label)
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx.Replace("<text>Chapter 1</text>", $"<text>{label}</text>", StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithResource("OPS/toc.ncx", ncx).Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal([2], result.Chapters.Select(chapter => chapter.Number));
    }

    [Fact]
    public async Task ParseAsync_ReadsCalibreSplitChaptersWithSections()
    {
        using var epub = new SyntheticEpubBuilder().AsCalibreSplitEpub2().Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal(
            [(BookItemKind.Section, 1, "Introduction", 1), (BookItemKind.Chapter, 1, null, 2), (BookItemKind.Chapter, 2, null, 3), (BookItemKind.Section, 2, "Epilogue", 4)],
            result.Chapters.Select(item => (item.Kind, item.Number, item.Title, item.Order)));
        Assert.Equal(["Synthetic introduction prose."], result.Chapters[0].Paragraphs);
        // The "Chapter 1" echo and the footnotes block are gone; the split_001 text follows.
        Assert.Equal(["Synthetic journeyman prose.", "More journeyman prose."], result.Chapters[1].Paragraphs);
        // Continuation stops at the next TOC-targeted file, so back matter never joins the last chapter.
        Assert.Equal(["Synthetic icarus prose."], result.Chapters[2].Paragraphs);
        Assert.Equal(["Synthetic epilogue prose."], result.Chapters[3].Paragraphs);
        Assert.DoesNotContain(result.Chapters.SelectMany(item => item.Paragraphs), text => text.Contains("thanks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_RejectsCalibreSplitWithoutChapterEvidence()
    {
        var builder = new SyntheticEpubBuilder().AsCalibreSplitEpub2();
        foreach (var path in new[] { "OPS/text/part0006_split_000.html", "OPS/text/part0007_split_000.html" })
        {
            var number = path.Contains("0006") ? "b6" : "b7";
            builder.WithResource(path, $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body id=\"{number}\"><div><p>Chapter</p></div></body></html>");
        }
        using var epub = builder.Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_KeepsUntypedNotesAndNonEchoFirstParagraphs()
    {
        var builder = new SyntheticEpubBuilder().AsCalibreSplitEpub2()
            .WithResource("OPS/text/part0006_split_000.html", "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body id=\"b6\"><section epub:type=\"chapter\"><p>1984 was a year.</p></section></body></html>")
            .WithResource("OPS/text/part0007_split_000.html", "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body id=\"b7\"><section epub:type=\"chapter\"><p>2.</p><p>Notes on the text.</p></section></body></html>");
        using var epub = builder.Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal("1984 was a year.", result.Chapters[1].Paragraphs[0]);
        Assert.Equal("Notes on the text.", result.Chapters[2].Paragraphs[0]);
    }

    [Fact]
    public async Task ParseAsync_ReadsWholeDocumentEpub3ChaptersAndSkipsOtherLinks()
    {
        using var epub = new SyntheticEpubBuilder().AsWholeDocumentEpub3().Build();

        var result = await CreateParser().ParseAsync(epub);

        Assert.Equal([1, 2, 3], result.Chapters.Select(chapter => chapter.Number));
        Assert.All(result.Chapters, chapter => Assert.Equal(BookItemKind.Chapter, chapter.Kind));
        Assert.Equal(["Synthetic room prose 1."], result.Chapters[0].Paragraphs);
        Assert.DoesNotContain(result.Chapters.SelectMany(chapter => chapter.Paragraphs), text => text.Contains("copyright", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ParseAsync_StillRejectsNavLinksWithQueryStrings()
    {
        var navigation = SyntheticEpubBuilder.WholeDocumentNavigation.Replace("href=\"c002.xhtml\"", "href=\"c002.xhtml?x=1\"", StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsWholeDocumentEpub3().WithResource("OPS/nav.xhtml", navigation).Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Theory]
    [InlineData("Introduction")]
    [InlineData("Epílogo")]
    [InlineData("Förord")]
    public async Task ParseAsync_TreatsKeywordLabelsAsSections(string label)
    {
        var ncx = SyntheticEpubBuilder.DefaultNcx.Replace(
            "<navLabel><text>Preface</text></navLabel><content src=\"text/not-in-manifest.xhtml\" />",
            $"<navLabel><text>{label}</text></navLabel><content src=\"text/front.xhtml\" />",
            StringComparison.Ordinal);
        var package = SyntheticEpubBuilder.Epub2Package
            .Replace("<item id=\"chapter1\"", "<item id=\"front\" href=\"text/front.xhtml\" media-type=\"application/xhtml+xml\" /><item id=\"chapter1\"", StringComparison.Ordinal)
            .Replace("<itemref idref=\"chapter1\" />", "<itemref idref=\"front\" /><itemref idref=\"chapter1\" />", StringComparison.Ordinal);
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithPackage(package).WithResource("OPS/toc.ncx", ncx)
            .WithResource("OPS/text/front.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>{label}</p><p>Synthetic front prose.</p></body></html>")
            .Build();

        var result = await CreateParser().ParseAsync(epub);

        var section = result.Chapters[0];
        Assert.Equal((BookItemKind.Section, label, 1), (section.Kind, section.Title, section.Order));
        Assert.Equal(["Synthetic front prose."], section.Paragraphs);
        Assert.Equal([1, 2], result.Chapters.Where(item => item.Kind == BookItemKind.Chapter).Select(item => item.Number));
    }

    [Fact]
    public async Task ParseAsync_RejectsBookWithSectionsButNoChapters()
    {
        var ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
              <navPoint id="i"><navLabel><text>Introduction</text></navLabel><content src="text/chapter1.xhtml" /></navPoint>
            </navMap></ncx>
            """;
        using var epub = new SyntheticEpubBuilder().AsEpub2Ncx().WithResource("OPS/toc.ncx", ncx).Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ParseAsync_RejectsPageNumberNavigationAsUnsupportedStructure()
    {
        using var epub = new SyntheticEpubBuilder().AsPageListEpub2().Build();

        var exception = await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ParseAsync(epub));

        Assert.Equal(EpubParseErrorKind.UnsupportedStructure, exception.Kind);
    }

    [Fact]
    public async Task ReadReadingOrderAsync_ReturnsSpineTextWithoutFootnotes()
    {
        using var pages = new SyntheticEpubBuilder().AsPageListEpub2().Build();
        using var calibre = new SyntheticEpubBuilder().AsCalibreSplitEpub2().Build();

        var text = await CreateParser().ReadReadingOrderAsync(pages);
        var calibreText = await CreateParser().ReadReadingOrderAsync(calibre);

        Assert.Equal(("Knight Sample", "eng"), (text.Title, text.Language));
        Assert.Equal(["SYNTHETIC KNIGHT", "Synthetic knight prose one.", "Synthetic knight prose two."], text.Paragraphs);
        Assert.Equal("Synthetic introduction prose.", calibreText.Paragraphs[0]);
        Assert.Equal("Synthetic thanks.", calibreText.Paragraphs[^1]);
        Assert.DoesNotContain(calibreText.Paragraphs, paragraph => paragraph.Contains("footnote", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadReadingOrderAsync_KeepsArchiveProtectionsAndLimits()
    {
        using var encrypted = new SyntheticEpubBuilder().AsPageListEpub2().WithEncryption().Build();
        using var notZip = new MemoryStream("plain"u8.ToArray());
        using var pages = new SyntheticEpubBuilder().AsPageListEpub2().Build();

        Assert.Equal(EpubParseErrorKind.UnsupportedContent,
            (await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ReadReadingOrderAsync(encrypted))).Kind);
        Assert.Equal(EpubParseErrorKind.InvalidEpub,
            (await Assert.ThrowsAsync<EpubParseException>(() => CreateParser().ReadReadingOrderAsync(notZip))).Kind);
        var limited = new EpubChapterParser(Options.Create(new EpubParserOptions { MaxBookCharacters = 10 }));
        Assert.Equal(EpubParseErrorKind.UnsupportedContent,
            (await Assert.ThrowsAsync<EpubParseException>(() => limited.ReadReadingOrderAsync(pages))).Kind);
    }

    private static EpubChapterParser CreateParser() => new(Options.Create(new EpubParserOptions()));

    private static MemoryStream BuildNumericNcxWithoutPackageLanguage(
        string? chapterOneLanguage,
        string? chapterTwoLanguage)
    {
        var package = RemovePackageLanguage(SyntheticEpubBuilder.Epub2Package);
        var ncx = SyntheticEpubBuilder.DefaultNcx
            .Replace("<text>Chapter 1</text>", "<text>1</text>", StringComparison.Ordinal)
            .Replace("<text>Chapter 2</text>", "<text>2</text>", StringComparison.Ordinal);
        var chapterOne = AddHeadingLanguage(SyntheticEpubBuilder.Epub2ChapterOne, "1", chapterOneLanguage);
        var chapterTwo = AddHeadingLanguage(SyntheticEpubBuilder.Epub2ChapterTwo, "2", chapterTwoLanguage);
        return new SyntheticEpubBuilder().AsEpub2Ncx()
            .WithPackage(package)
            .WithResource("OPS/toc.ncx", ncx)
            .WithResource("OPS/text/chapter1.xhtml", chapterOne)
            .WithResource("OPS/text/chapter2.xhtml", chapterTwo)
            .Build();
    }

    private static string RemovePackageLanguage(string package) =>
        package.Replace("<dc:language>en-US</dc:language>", string.Empty, StringComparison.Ordinal)
            .Replace("<dc:language>en</dc:language>", string.Empty, StringComparison.Ordinal);

    private static string AddHeadingLanguage(string chapter, string number, string? language) =>
        language is null
            ? chapter
            : chapter.Replace(
                $"<h2>{number}</h2>",
                $"<h2 xml:lang=\"{language}\">{number}</h2>",
                StringComparison.Ordinal);
}

internal sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
{
    public override bool CanSeek => false;
}
