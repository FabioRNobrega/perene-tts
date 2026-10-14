using System.Text;
namespace PereneTts.Models;

/// <summary>Pure EPUB import rules: batch-name prefill, spoken labels, track names, language checks, and track limits.</summary>
public static class EpubBookRules
{
    public const int MaxIntroCharacters = 5000;
    public const string NoChaptersMessage = "Include at least one chapter.";

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
        _ => $"chapter-{number:000}.txt"
    };

    /// <summary>Lower-case primary subtag of a BCP 47 tag: pt-BR → pt; empty when missing.</summary>
    public static string PrimaryLanguage(string? bcp47)
    {
        var tag = (bcp47 ?? "").Trim();
        var end = tag.IndexOfAny(['-', '_']);
        return (end < 0 ? tag : tag[..end]).ToLowerInvariant();
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

    public static IReadOnlyList<string> TrackErrors(IReadOnlyList<BookTrack> tracks)
    {
        var errors = new List<string>();
        if (!tracks.Any(track => track.ChapterNumber is not null)) errors.Add(NoChaptersMessage);
        errors.AddRange(tracks.Select(TrackError).OfType<string>());
        if (BatchRules.SelectionError(tracks.Count, tracks.Sum(track => (long)track.Bytes)) is { } selection) errors.Add(selection);
        return errors;
    }
}
