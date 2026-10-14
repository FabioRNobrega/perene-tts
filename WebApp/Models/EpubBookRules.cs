using System.Globalization;
using System.Text;
namespace PereneTts.Models;

/// <summary>Pure EPUB import rules: batch-name prefill, spoken labels, track names, language checks, and track limits.</summary>
public static class EpubBookRules
{
    public const int MaxIntroCharacters = 5000;
    public const string NoChaptersMessage = "Include at least one chapter.";
    public const string NoPartsMessage = "Include at least one part.";

    /// <summary>EPUB 3 structural semantics that mark an unnumbered narrative section.</summary>
    public static readonly IReadOnlySet<string> SectionTypes =
        new HashSet<string>(["introduction", "preface", "foreword", "prologue", "epilogue", "afterword", "conclusion"], StringComparer.OrdinalIgnoreCase);

    // en, pt, and sv section labels, compared without diacritics.
    private static readonly HashSet<string> SectionLabels = new(
        ["introduction", "preface", "foreword", "prologue", "epilogue", "afterword", "conclusion",
         "introducao", "prefacio", "prologo", "epilogo", "posfacio", "conclusao",
         "inledning", "forord", "prolog", "epilog", "efterord"],
        StringComparer.Ordinal);

    // ISO 639-2 codes for the studio languages.
    private static readonly Dictionary<string, string> ThreeLetterLanguages = new(StringComparer.Ordinal)
    {
        ["eng"] = "en",
        ["por"] = "pt",
        ["swe"] = "sv"
    };

    /// <summary>Title without /, \ or control characters, whitespace collapsed, truncated to the batch-name limit.</summary>
    public static string DefaultBatchName(string? title)
    {
        var builder = new StringBuilder();
        foreach (var character in title ?? "")
        {
            // Tabs and line breaks separate words, so they collapse to a space before control characters are dropped.
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }
            if (character is '/' or '\\' || char.IsControl(character)) continue;
            builder.Append(character);
        }
        var name = builder.ToString().Trim();
        return name.Length <= BatchRules.MaxNameLength ? name : name[..BatchRules.MaxNameLength].TrimEnd();
    }

    public static string ChapterLabel(string languageId) => languageId switch
    {
        "en" => "Chapter",
        "pt" => "Capítulo",
        "sv" => "Kapitel",
        _ => throw new ArgumentOutOfRangeException(nameof(languageId), languageId, "Unsupported narration language.")
    };

    public static string TrackFileName(BookTrackKind kind, int number = 0) => kind switch
    {
        BookTrackKind.Intro => "intro.txt",
        BookTrackKind.Outro => "outro.txt",
        BookTrackKind.Part => $"part-{number:000}.txt",
        BookTrackKind.Chapter => $"chapter-{number:000}.txt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Sections are named from their title.")
    };

    /// <summary>
    /// A section's upload name from its title: a lowercase ASCII slug, never intro, outro, chapter-*, or part-*,
    /// and unique within the book (adds -2, -3 …). Records the name in <paramref name="used"/>.
    /// </summary>
    public static string SectionFileName(string title, int ordinal, ISet<string> used)
    {
        var slug = Slug(title);
        if (slug.Length == 0) slug = $"section-{ordinal:00}";
        var name = slug;
        for (var suffix = 2; used.Contains(name + ".txt") || IsReservedTrackName(name); suffix++) name = $"{slug}-{suffix}";
        used.Add(name + ".txt");
        return name + ".txt";
    }

    private static bool IsReservedTrackName(string stem) =>
        stem is "intro" or "outro"
        || (stem.StartsWith("chapter-", StringComparison.Ordinal) && stem.Length > 8 && stem[8..].All(char.IsAsciiDigit))
        || (stem.StartsWith("part-", StringComparison.Ordinal) && stem.Length > 5 && stem[5..].All(char.IsAsciiDigit));

    /// <summary>Ported from book-notes-ia ChapterOutputWriter.CreateSlug: diacritics removed, ASCII letters/digits joined by '-'.</summary>
    public static string Slug(string? title)
    {
        var builder = new StringBuilder();
        var pendingSeparator = false;
        foreach (var character in (title ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (character <= 127 && char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0) builder.Append('-');
                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }
        return builder.ToString();
    }

    /// <summary>True when a table-of-contents label names a narrative section (Introduction, Epílogo, Förord …).</summary>
    public static bool SectionKeyword(string? label)
    {
        var trimmed = (label ?? "").Trim().TrimEnd('.', ':', '!', '?', '…').Trim();
        return trimmed.Length > 0 && SectionLabels.Contains(Slug(trimmed));
    }

    /// <summary>The spoken section heading: the title with a closing period unless it already ends a sentence.</summary>
    public static string SectionHeading(string title)
    {
        var heading = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return heading.Length > 0 && heading[^1] is '.' or '!' or '?' or '…' ? heading : heading + ".";
    }

    /// <summary>
    /// Fixed-size fallback: n = ceil(total / target) balanced parts, filled in order at paragraph boundaries.
    /// A paragraph is never split, so one longer than a part stays whole.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> SplitIntoParts(IReadOnlyList<string> paragraphs, int targetCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetCharacters);
        var total = paragraphs.Sum(paragraph => (long)paragraph.Length);
        if (total == 0) return [];
        var count = (int)((total + targetCharacters - 1) / targetCharacters);
        var size = (double)total / count;
        var parts = new List<IReadOnlyList<string>>(count);
        var current = new List<string>();
        long filled = 0;
        foreach (var paragraph in paragraphs)
        {
            current.Add(paragraph);
            filled += paragraph.Length;
            // Close a part once the running total reaches its share, so rounding never leaves a tiny last part.
            if (parts.Count < count - 1 && filled >= size * (parts.Count + 1))
            {
                parts.Add(current);
                current = [];
            }
        }
        if (current.Count > 0) parts.Add(current);
        return parts;
    }

    /// <summary>Lower-case primary subtag of a language tag: pt-BR → pt, eng → en; empty when missing.</summary>
    public static string PrimaryLanguage(string? bcp47)
    {
        var tag = (bcp47 ?? "").Trim();
        var end = tag.IndexOfAny(['-', '_']);
        var primary = (end < 0 ? tag : tag[..end]).ToLowerInvariant();
        return ThreeLetterLanguages.GetValueOrDefault(primary, primary);
    }

    /// <summary>True only when both languages are known and their primary subtags differ.</summary>
    public static bool LanguageMismatch(string? bookLanguage, string? voiceLanguage)
    {
        var book = PrimaryLanguage(bookLanguage);
        var voice = PrimaryLanguage(voiceLanguage);
        return book.Length > 0 && voice.Length > 0 && book != voice;
    }

    public static string? TextError(string label, string? text) =>
        (text?.Length ?? 0) > MaxIntroCharacters ? $"{label} must be 5,000 characters or fewer." : null;

    /// <summary>The same per-file pre-check New batch uses, applied to generated text.</summary>
    public static string? TrackError(BookTrack track) => BatchRules.FileError(track.DisplayName, track.Bytes, track.Characters);

    public static IReadOnlyList<string> TrackErrors(IReadOnlyList<BookTrack> tracks, BookStructure structure = BookStructure.Chapters)
    {
        var errors = new List<string>();
        if (!tracks.Any(track => track.ItemOrder is not null))
            errors.Add(structure == BookStructure.Parts ? NoPartsMessage : NoChaptersMessage);
        errors.AddRange(tracks.Select(TrackError).OfType<string>());
        if (BatchRules.SelectionError(tracks.Count, tracks.Sum(track => (long)track.Bytes)) is { } selection) errors.Add(selection);
        return errors;
    }
}
