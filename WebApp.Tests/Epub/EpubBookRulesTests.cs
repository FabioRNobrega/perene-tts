using PereneTts.Models;

namespace WebApp.Tests.Epub;

public sealed class EpubBookRulesTests
{
    [Theory]
    [InlineData("Á Sample Book", "Á Sample Book")]
    [InlineData("  Either/Or \\ Part\tOne\u0007 ", "EitherOr Part One")]
    [InlineData("", "")]
    public void DefaultBatchNameRemovesUnsafeCharacters(string title, string expected)
    {
        var name = EpubBookRules.DefaultBatchName(title);

        Assert.Equal(expected, name);
        if (name.Length > 0) Assert.Null(BatchRules.NameError(name));
    }

    [Fact]
    public void DefaultBatchNameTruncatesToTheBatchNameLimit()
    {
        var name = EpubBookRules.DefaultBatchName(new string('a', 120));

        Assert.Equal(80, name.Length);
        Assert.Null(BatchRules.NameError(name));
    }

    [Theory]
    [InlineData("en", "Chapter")]
    [InlineData("pt", "Capítulo")]
    [InlineData("sv", "Kapitel")]
    public void ChapterLabelFollowsTheLanguage(string language, string label)
    {
        Assert.Equal(label, EpubBookRules.ChapterLabel(language));
    }

    [Fact]
    public void ChapterLabelRejectsUnsupportedLanguages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EpubBookRules.ChapterLabel("de"));
    }

    [Fact]
    public void TrackFileNamesArePaddedAndPositionIndependent()
    {
        Assert.Equal("intro.txt", EpubBookRules.TrackFileName(BookTrackKind.Intro));
        Assert.Equal("chapter-007.txt", EpubBookRules.TrackFileName(BookTrackKind.Chapter, 7));
        Assert.Equal("chapter-124.txt", EpubBookRules.TrackFileName(BookTrackKind.Chapter, 124));
        Assert.Equal("outro.txt", EpubBookRules.TrackFileName(BookTrackKind.Outro));
    }

    [Theory]
    [InlineData("pt-BR", "pt")]
    [InlineData("en_GB", "en")]
    [InlineData(" SV ", "sv")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void PrimaryLanguageIsTheLowerCasePrimarySubtag(string? tag, string expected)
    {
        Assert.Equal(expected, EpubBookRules.PrimaryLanguage(tag));
    }

    [Theory]
    [InlineData("pt-BR", "pt", false)]
    [InlineData("en-GB", "pt", true)]
    [InlineData("pt-BR", "en", true)]
    [InlineData("", "pt", false)]
    [InlineData("sv", "", false)]
    public void LanguageMismatchComparesPrimarySubtags(string book, string voice, bool mismatch)
    {
        Assert.Equal(mismatch, EpubBookRules.LanguageMismatch(book, voice));
    }

    [Fact]
    public void TextErrorAllowsFiveThousandCharacters()
    {
        Assert.Null(EpubBookRules.TextError("Intro", new string('a', 5000)));
        Assert.Equal("Intro must be 5,000 characters or fewer.", EpubBookRules.TextError("Intro", new string('a', 5001)));
    }

    private static BookTrack Chapter(int number, int characters = 10, int bytes = 10) =>
        new(EpubBookRules.TrackFileName(BookTrackKind.Chapter, number), "", characters, bytes, number);

    [Fact]
    public void ValidTracksHaveNoErrors()
    {
        Assert.Empty(EpubBookRules.TrackErrors([new("intro.txt", "", 5, 5, null), Chapter(1), Chapter(2)]));
    }

    [Fact]
    public void TrackErrorsRequireAChapter()
    {
        Assert.Equal([EpubBookRules.NoChaptersMessage], EpubBookRules.TrackErrors([new("intro.txt", "", 5, 5, null)]));
        Assert.Equal([EpubBookRules.NoChaptersMessage], EpubBookRules.TrackErrors([]));
    }

    [Fact]
    public void TrackErrorsReuseBatchFileLimits()
    {
        Assert.Equal(["chapter-001.txt: file exceeds 200,000 characters"], EpubBookRules.TrackErrors([Chapter(1, characters: 200_001)]));
        Assert.Equal(["chapter-002.txt: file exceeds 1 MiB"], EpubBookRules.TrackErrors([Chapter(2, bytes: BatchRules.MaxFileBytes + 1)]));
    }

    [Fact]
    public void TrackErrorsReuseBatchSelectionLimits()
    {
        var many = Enumerable.Range(1, 201).Select(number => Chapter(number)).ToList();
        Assert.Equal(["A batch may contain at most 200 files."], EpubBookRules.TrackErrors(many));

        var perTrack = (int)(BatchRules.MaxTotalBytes / 10);
        var large = Enumerable.Range(1, 10).Select(number => Chapter(number, bytes: perTrack)).Append(Chapter(11, bytes: 1)).ToList();
        Assert.Equal(["The batch exceeds 10 MiB in total."], EpubBookRules.TrackErrors(large));
    }
}
