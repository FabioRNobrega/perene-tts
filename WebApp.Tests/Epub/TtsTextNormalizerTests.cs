using PereneTts.Services.Epub;

// Ported from book-notes-ia/services/EbookParseService.Tests/TtsTextNormalizerTests.cs, plus Swedish and language-neutral cases.
namespace WebApp.Tests.Epub;

public sealed class TtsTextNormalizerTests
{
    private readonly TtsTextNormalizer _normalizer = new();

    [Fact]
    public void Normalize_RemovesOnlyStandaloneSceneSeparator()
    {
        string[] paragraphs = ["Before.", "  ***  ", "Keep *** inside.", "After."];

        var result = _normalizer.NormalizeParagraphs(paragraphs, "en");

        Assert.Equal(["Before.", "Keep *** inside.", "After."], result);
        Assert.Equal(["Before.", "  ***  ", "Keep *** inside.", "After."], paragraphs);
    }

    [Theory]
    [InlineData("– Não sei – disse Case.", "Não sei, disse Case.")]
    [InlineData("— Pare! — Ele correu.", "Pare! Ele correu.")]
    [InlineData("– Talvez? – perguntou Molly.", "Talvez? perguntou Molly.")]
    [InlineData("– Espere… – disse ele.", "Espere… disse ele.")]
    public void Normalize_AppliesPortugueseDialogueRules(string input, string expected)
    {
        Assert.Equal(expected, Assert.Single(_normalizer.NormalizeParagraphs([input], "pt")));
    }

    [Theory]
    [InlineData("– Jaha. Vad gör vi nu då? sa Irene till Tommy.", "Jaha. Vad gör vi nu då? sa Irene till Tommy.")]
    [InlineData("– Kom hit, sa han. – Nu!", "Kom hit, sa han. Nu!")]
    [InlineData("– Vänta – sa hon.", "Vänta, sa hon.")]
    [InlineData("— Stopp! — Han sprang.", "Stopp! Han sprang.")]
    public void Normalize_AppliesSwedishDialogueRules(string input, string expected)
    {
        Assert.Equal(expected, Assert.Single(_normalizer.NormalizeParagraphs([input], "sv")));
    }

    [Theory]
    [InlineData("– Jaha. Vad gör vi nu då? sa Irene till Tommy.")]
    [InlineData("– Não sei – disse Case.")]
    [InlineData("— Pare! — Ele correu.")]
    public void Normalize_SwedishAndPortugueseShareDialogueRules(string input)
    {
        Assert.Equal(_normalizer.NormalizeParagraphs([input], "pt"), _normalizer.NormalizeParagraphs([input], "sv"));
    }

    [Fact]
    public void Normalize_SwedishRemovesSceneSeparatorAndNormalizesPeriodsAndWhitespace()
    {
        var result = _normalizer.NormalizeParagraphs(["***", "  Vänta...  där.\t ", "Slut...."], "sv");

        Assert.Equal(["Vänta, där.", "Slut."], result);
    }

    [Fact]
    public void Normalize_PreservesEnglishDialogueDashes()
    {
        var result = _normalizer.NormalizeParagraphs(["  – Hello – she said.  "], "en");

        Assert.Equal("– Hello – she said.", Assert.Single(result));
    }

    [Fact]
    public void Normalize_WithoutLanguageAppliesOnlyNeutralRules()
    {
        var result = _normalizer.NormalizeParagraphs(["***", "– Wait...  there – he said."], null);

        Assert.Equal("– Wait, there – he said.", Assert.Single(result));
    }

    [Theory]
    [InlineData("Wait... there.", "Wait, there.")]
    [InlineData("Finished....", "Finished.")]
    [InlineData("One..... two...", "One, two.")]
    public void Normalize_NormalizesRepeatedPeriods(string input, string expected)
    {
        Assert.Equal(expected, Assert.Single(_normalizer.NormalizeParagraphs([input], "en")));
    }

    [Fact]
    public void Normalize_CollapsesHorizontalWhitespaceWithoutMergingParagraphs()
    {
        var result = _normalizer.NormalizeParagraphs(["  First\t\t paragraph.  ", "  Second  paragraph. "], "en");

        Assert.Equal(["First paragraph.", "Second paragraph."], result);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("de")]
    [InlineData("")]
    public void Normalize_RejectsUnsupportedLanguage(string language)
    {
        var exception = Assert.Throws<EpubParseException>(() => _normalizer.NormalizeParagraphs(["Text."], language));

        Assert.Equal(EpubParseErrorKind.InvalidRequest, exception.Kind);
        Assert.Equal("The narration language must be en, pt, or sv.", exception.PublicMessage);
    }

    // The reference threw here; the port returns nothing so track validation can mark the chapter as empty.
    [Fact]
    public void Normalize_ReturnsNoParagraphsWhenOnlySceneSeparatorsRemain()
    {
        Assert.Empty(_normalizer.NormalizeParagraphs(["***"], "pt"));
    }
}
