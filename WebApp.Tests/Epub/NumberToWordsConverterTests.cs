using PereneTts.Services.Epub;

// Ported from book-notes-ia/services/EbookParseService.Tests/NumberToWordsConverterTests.cs, plus Swedish.
namespace WebApp.Tests.Epub;

public sealed class NumberToWordsConverterTests
{
    private readonly NumberToWordsConverter _converter = new();

    [Theory]
    [InlineData(0, "Zero")]
    [InlineData(1, "One")]
    [InlineData(11, "Eleven")]
    [InlineData(19, "Nineteen")]
    [InlineData(20, "Twenty")]
    [InlineData(24, "Twenty-four")]
    [InlineData(40, "Forty")]
    [InlineData(99, "Ninety-nine")]
    [InlineData(100, "One hundred")]
    [InlineData(101, "One hundred one")]
    [InlineData(115, "One hundred fifteen")]
    [InlineData(124, "One hundred twenty-four")]
    [InlineData(200, "Two hundred")]
    [InlineData(999, "Nine hundred ninety-nine")]
    public void TryConvert_UsesNaturalEnglishCardinalWords(int number, string expected)
    {
        var converted = _converter.TryConvert(number, "en", out var words);

        Assert.True(converted);
        Assert.Equal(expected, words);
        Assert.DoesNotContain(" and ", words, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Zero")]
    [InlineData(1, "Um")]
    [InlineData(11, "Onze")]
    [InlineData(19, "Dezenove")]
    [InlineData(20, "Vinte")]
    [InlineData(24, "Vinte e quatro")]
    [InlineData(99, "Noventa e nove")]
    [InlineData(100, "Cem")]
    [InlineData(101, "Cento e um")]
    [InlineData(115, "Cento e quinze")]
    [InlineData(200, "Duzentos")]
    [InlineData(300, "Trezentos")]
    [InlineData(400, "Quatrocentos")]
    [InlineData(500, "Quinhentos")]
    [InlineData(600, "Seiscentos")]
    [InlineData(700, "Setecentos")]
    [InlineData(800, "Oitocentos")]
    [InlineData(900, "Novecentos")]
    [InlineData(999, "Novecentos e noventa e nove")]
    public void TryConvert_UsesNaturalPortugueseCardinalWords(int number, string expected)
    {
        var converted = _converter.TryConvert(number, "pt", out var words);

        Assert.True(converted);
        Assert.Equal(expected, words);
    }

    [Theory]
    [InlineData(0, "Noll")]
    [InlineData(1, "Ett")]
    [InlineData(17, "Sjutton")]
    [InlineData(20, "Tjugo")]
    [InlineData(24, "Tjugofyra")]
    [InlineData(80, "Åttio")]
    [InlineData(100, "Etthundra")]
    [InlineData(101, "Etthundraett")]
    [InlineData(124, "Etthundratjugofyra")]
    [InlineData(200, "Tvåhundra")]
    [InlineData(999, "Niohundranittionio")]
    public void TryConvert_UsesSwedishCompoundCardinalWords(int number, string expected)
    {
        var converted = _converter.TryConvert(number, "sv", out var words);

        Assert.True(converted);
        Assert.Equal(expected, words);
        Assert.DoesNotContain(" ", words, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt")]
    [InlineData("sv")]
    public void TryConvert_SupportsEveryNumberFromZeroThrough999(string language)
    {
        for (var number = 0; number <= 999; number++)
        {
            Assert.True(_converter.TryConvert(number, language, out var words));
            Assert.False(string.IsNullOrWhiteSpace(words));
        }
    }

    [Theory]
    [InlineData(-1, "en")]
    [InlineData(1000, "en")]
    [InlineData(-1, "sv")]
    [InlineData(1000, "sv")]
    [InlineData(1, "sv-SE")]
    [InlineData(1, "eng")]
    [InlineData(1, "pt-BR")]
    [InlineData(1, "")]
    [InlineData(1, null)]
    public void TryConvert_RejectsUnsupportedInput(int number, string? language)
    {
        var converted = _converter.TryConvert(number, language, out var words);

        Assert.False(converted);
        Assert.Equal(string.Empty, words);
    }
}
