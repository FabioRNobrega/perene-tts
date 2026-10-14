using System.Text;
using PereneTts.Models;
using PereneTts.Services.Epub;

namespace WebApp.Tests.Epub;

public sealed class BookTrackBuilderTests
{
    private readonly BookTrackBuilder _builder = new(new TtsTextNormalizer(), new NumberToWordsConverter());

    private static ParsedEpubBook Book(params int[] numbers) =>
        new("Book", "en", numbers.Select((number, index) =>
            new ParsedChapter(number, [$"Text of chapter {number}.", "Second  paragraph."], Order: index + 1)).ToList());

    [Theory]
    [InlineData("en", "Chapter Twenty-four.")]
    [InlineData("pt", "Capítulo Vinte e quatro.")]
    [InlineData("sv", "Kapitel Tjugofyra.")]
    public void ChapterTextStartsWithTheSpokenHeadingInTheVoiceLanguage(string language, string heading)
    {
        var text = _builder.ChapterText(new ParsedChapter(24, ["Body."]), language);

        Assert.Equal($"{heading}\n\nBody.\n", text);
    }

    [Fact]
    public void ChapterNumbersOutsideTheWordRangeUseDigits()
    {
        Assert.StartsWith("Chapter 1000.\n\n", _builder.ChapterText(new ParsedChapter(1000, ["Body."]), "en"));
    }

    [Fact]
    public void ChapterTextWithoutLanguageHasNoHeading()
    {
        Assert.Equal("Body.\n\nMore.\n", _builder.ChapterText(new ParsedChapter(3, ["Body.", "***", "More."]), null));
    }

    [Fact]
    public void ParagraphsAreJoinedWithBlankLinesAndEndWithNewline()
    {
        var text = _builder.ChapterText(new ParsedChapter(1, ["One.", "Two  words.", "Three."]), "en");

        Assert.Equal("Chapter One.\n\nOne.\n\nTwo words.\n\nThree.\n", text);
    }

    [Fact]
    public void ChapterLanguageRulesFollowTheVoice()
    {
        var chapter = new ParsedChapter(2, ["– Jaha. Vad gör vi nu då? sa Irene till Tommy."]);

        Assert.Equal("Kapitel Två.\n\nJaha. Vad gör vi nu då? sa Irene till Tommy.\n", _builder.ChapterText(chapter, "sv"));
        Assert.Contains("– Jaha.", _builder.ChapterText(chapter, "en"), StringComparison.Ordinal);
    }

    [Fact]
    public void IntroAndOutroKeepTheirTextWithNormalizedWhitespace()
    {
        var tracks = _builder.Build(Book(1), new BookSelection("  Read  by\tme.\r\nWelcome – friend...  ", " The end. ", new HashSet<int>()), "pt");

        Assert.Equal("Read by me.\nWelcome – friend...", tracks[0].Text);
        Assert.Equal("The end.", tracks[^1].Text);
        Assert.Null(tracks[0].ItemOrder);
    }

    [Fact]
    public void BlankIntroAndOutroAddNoTracks()
    {
        var tracks = _builder.Build(Book(1, 2), new BookSelection(" \n\t", "", new HashSet<int>()), "en");

        Assert.Equal(["chapter-001.txt", "chapter-002.txt"], tracks.Select(track => track.DisplayName));
    }

    [Fact]
    public void SectionsAndPartsUseTheirOwnHeadingsAndNames()
    {
        var book = new ParsedEpubBook("Book", "en",
        [
            new ParsedChapter(1, ["Opening words."], BookItemKind.Section, "Introduction", 1),
            new ParsedChapter(1, ["Body."], Order: 2),
            new ParsedChapter(2, ["Closing words."], BookItemKind.Section, "What Now?", 3)
        ]);

        var tracks = _builder.ItemTracks(book, "en");

        Assert.Equal(["introduction.txt", "chapter-001.txt", "what-now.txt"], tracks.Select(track => track.DisplayName));
        Assert.Equal("Introduction.\n\nOpening words.\n", tracks[0].Text);
        Assert.Equal("What Now?\n\nClosing words.\n", tracks[2].Text);
        Assert.Equal([BookItemKind.Section, BookItemKind.Chapter, BookItemKind.Section], tracks.Select(track => track.Kind!.Value));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt")]
    [InlineData(null)]
    public void PartsHaveNoSpokenHeading(string? language)
    {
        var part = new ParsedChapter(2, ["Mid-scene sentence."], BookItemKind.Part, null, 2);

        Assert.Equal("Mid-scene sentence.\n", _builder.ChapterText(part, language));
        Assert.Equal("part-002.txt", _builder.ItemTracks(new ParsedEpubBook("B", "", [part], BookStructure.Parts), language)[0].DisplayName);
    }

    [Fact]
    public void ExclusionIsKeyedByBookOrderNotChapterNumber()
    {
        var book = new ParsedEpubBook("Book", "en",
        [
            new ParsedChapter(1, ["Intro words."], BookItemKind.Section, "Introduction", 1),
            new ParsedChapter(1, ["Chapter words."], Order: 2)
        ]);

        var tracks = _builder.Build(book, new BookSelection("", "", new HashSet<int> { 1 }), "en");

        Assert.Equal(["chapter-001.txt"], tracks.Select(track => track.DisplayName));
    }

    [Fact]
    public void OrderAndDisplayNamesSkipExcludedChapters()
    {
        var tracks = _builder.Build(Book(1, 2, 3), new BookSelection("Hello.", "Bye.", new HashSet<int> { 2 }), "en");

        Assert.Equal(["intro.txt", "chapter-001.txt", "chapter-003.txt", "outro.txt"], tracks.Select(track => track.DisplayName));
        Assert.Equal([null, 1, 3, null], tracks.Select(track => track.ItemOrder));
        Assert.Equal(["Batch 001.mp3", "Batch 002.mp3", "Batch 003.mp3", "Batch 004.mp3"],
            tracks.Select((_, index) => BatchRules.OutputName("Batch", index + 1)));
    }

    [Fact]
    public void PrecomputedChapterTracksBuildTheSameList()
    {
        var book = Book(1, 2, 3);
        var selection = new BookSelection("Hi.", "", new HashSet<int> { 1 });
        var itemTracks = _builder.ItemTracks(book, "sv");

        Assert.Equal(_builder.Build(book, selection, "sv"), _builder.Build(itemTracks, selection));
    }

    [Fact]
    public void CharactersAndBytesMatchTheUploadedText()
    {
        var track = _builder.ItemTracks(new ParsedEpubBook("B", "pt", [new ParsedChapter(1, ["Olá, ação!"], Order: 1)]), "pt")[0];
        var bytes = Encoding.UTF8.GetBytes(track.Text);

        Assert.Equal(bytes.Length, track.Bytes);
        Assert.Equal(track.Text.Trim().Length, track.Characters);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.True(track.Bytes > track.Characters);
    }

    [Fact]
    public void ChapterWithNothingNarratableBecomesAnEmptyTrack()
    {
        var track = _builder.ItemTracks(new ParsedEpubBook("B", "en", [new ParsedChapter(4, ["***"], Order: 1)]), "en")[0];

        Assert.Equal(0, track.Characters);
        Assert.Equal("chapter-004.txt: file is empty", EpubBookRules.TrackError(track));
    }
}
