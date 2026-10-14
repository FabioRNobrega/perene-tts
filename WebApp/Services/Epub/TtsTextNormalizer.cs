using System.Text.RegularExpressions;

// Ported from book-notes-ia/services/EbookParseService.Api/Services/TtsTextNormalizer.cs.
// Works per chapter so previews follow the chosen voice; Swedish shares the Portuguese dialogue-dash rules.
namespace PereneTts.Services.Epub;

public sealed class TtsTextNormalizer : ITtsTextNormalizer
{
    private static readonly Regex HorizontalWhitespace = new(
        @"[\p{Zs}\t\f\v]+",
        RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedPeriods = new(
        @"\.{3,}",
        RegexOptions.CultureInvariant);

    private static readonly Regex LeadingDialogueDash = new(
        @"^[–—]\s*",
        RegexOptions.CultureInvariant);

    private static readonly Regex InlineDialogueDash = new(
        @"\s+[–—]\s+",
        RegexOptions.CultureInvariant);

    public static bool IsSupported(string? language) => language is "en" or "pt" or "sv";

    /// <summary>An empty result means nothing narratable remained; track validation reports it as empty.</summary>
    public IReadOnlyList<string> NormalizeParagraphs(IReadOnlyList<string> paragraphs, string? languageId)
    {
        if (languageId is not null && !IsSupported(languageId))
        {
            throw new EpubParseException(
                EpubParseErrorKind.InvalidRequest,
                "The narration language must be en, pt, or sv.");
        }

        return paragraphs
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => paragraph != "***")
            .Select(paragraph => NormalizeParagraph(paragraph, languageId))
            .Where(paragraph => paragraph.Length > 0)
            .ToArray();
    }

    private static string NormalizeParagraph(string paragraph, string? language)
    {
        var normalized = HorizontalWhitespace.Replace(paragraph, " ").Trim();
        normalized = RepeatedPeriods.Replace(normalized, match =>
            HasFollowingText(normalized, match.Index + match.Length) ? "," : ".");

        if (language is "pt" or "sv")
        {
            normalized = LeadingDialogueDash.Replace(normalized, string.Empty);
            normalized = InlineDialogueDash.Replace(normalized, match =>
                HasTerminalPunctuation(normalized, match.Index) ? " " : ", ");
        }

        return HorizontalWhitespace.Replace(normalized, " ").Trim();
    }

    private static bool HasFollowingText(string value, int startIndex)
    {
        for (var index = startIndex; index < value.Length; index++)
        {
            if (!char.IsWhiteSpace(value[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTerminalPunctuation(string value, int endIndex)
    {
        for (var index = endIndex - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                continue;
            }

            return value[index] is '.' or '!' or '?' or '…';
        }

        return false;
    }
}
