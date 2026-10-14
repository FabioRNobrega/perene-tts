using System.Text;
using PereneTts.Models;
using PereneTts.Services.Epub;

namespace WebApp.Tests.Epub;

public sealed class BookTrackBuilderTests
{
    private readonly BookTrackBuilder _builder = new(new TtsTextNormalizer(), new NumberToWordsConverter());

    private static ParsedEpubBook Book(params int[] numbers) =>
        new("Book", "en", numbers.Select(number => new ParsedChapter(number, [$"Text of chapter {number}.", "Second  paragraph."])).ToList());

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
        Assert.Null(tracks[0].ChapterNumber);
    }

    [Fact]
    public void BlankIntroAndOutroAddNoTracks()
    {
        var tracks = _builder.Build(Book(1, 2), new BookSelection(" \n\t", "", new HashSet<int>()), "en");

        Assert.Equal(["chapter-001.txt", "chapter-002.txt"], tracks.Select(track => track.DisplayName));
    }

    [Fact]
    public void OrderAndDisplayNamesSkipExcludedChapters()
    {
        var tracks = _builder.Build(Book(3, 1, 2), new BookSelection("Hello.", "Bye.", new HashSet<int> { 2 }), "en");

        Assert.Equal(["intro.txt", "chapter-001.txt", "chapter-003.txt", "outro.txt"], tracks.Select(track => track.DisplayName));
        Assert.Equal([null, 1, 3, null], tracks.Select(track => track.ChapterNumber));
        Assert.Equal(["Batch 001.mp3", "Batch 002.mp3", "Batch 003.mp3", "Batch 004.mp3"],
            tracks.Select((_, index) => BatchRules.OutputName("Batch", index + 1)));
    }

    [Fact]
    public void PrecomputedChapterTracksBuildTheSameList()
    {
        var book = Book(1, 2, 3);
        var selection = new BookSelection("Hi.", "", new HashSet<int> { 1 });
        var chapterTracks = book.Chapters.Select(chapter => _builder.ChapterTrack(chapter, "sv")).ToList();

        Assert.Equal(_builder.Build(book, selection, "sv"), _builder.Build(chapterTracks, selection));
    }

    [Fact]
    public void CharactersAndBytesMatchTheUploadedText()
    {
        var track = _builder.ChapterTrack(new ParsedChapter(1, ["Olá, ação!"]), "pt");
        var bytes = Encoding.UTF8.GetBytes(track.Text);

        Assert.Equal(bytes.Length, track.Bytes);
        Assert.Equal(track.Text.Trim().Length, track.Characters);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.True(track.Bytes > track.Characters);
    }

    [Fact]
    public void ChapterWithNothingNarratableBecomesAnEmptyTrack()
    {
        var track = _builder.ChapterTrack(new ParsedChapter(4, ["***"]), "en");

        Assert.Equal(0, track.Characters);
        Assert.Equal("chapter-004.txt: file is empty", EpubBookRules.TrackError(track));
    }
}
