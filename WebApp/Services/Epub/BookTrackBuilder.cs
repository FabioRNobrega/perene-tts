using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PereneTts.Models;

// Rendering ported from book-notes-ia/services/EbookParseService.Api/Services/ChapterOutputWriter.cs (Render only).
namespace PereneTts.Services.Epub;

public sealed partial class BookTrackBuilder(ITtsTextNormalizer normalizer, INumberToWordsConverter numbers) : IBookTrackBuilder
{
    public string ChapterText(ParsedChapter chapter, string? languageId)
    {
        var paragraphs = normalizer.NormalizeParagraphs(chapter.Paragraphs, languageId);
        if (paragraphs.Count == 0)
        {
            return string.Empty;
        }

        var body = string.Join("\n\n", paragraphs) + "\n";
        if (languageId is null)
        {
            return body;
        }

        var number = numbers.TryConvert(chapter.Number, languageId, out var words)
            ? words
            : chapter.Number.ToString(CultureInfo.InvariantCulture);
        return $"{EpubBookRules.ChapterLabel(languageId)} {number}.\n\n{body}";
    }

    public BookTrack ChapterTrack(ParsedChapter chapter, string? languageId) =>
        Track(EpubBookRules.TrackFileName(BookTrackKind.Chapter, chapter.Number), ChapterText(chapter, languageId), chapter.Number);

    public IReadOnlyList<BookTrack> Build(ParsedEpubBook book, BookSelection selection, string? languageId) =>
        Build(book.Chapters.Where(chapter => !selection.Excluded.Contains(chapter.Number))
            .Select(chapter => ChapterTrack(chapter, languageId)).ToList(), selection);

    public IReadOnlyList<BookTrack> Build(IReadOnlyList<BookTrack> chapterTracks, BookSelection selection)
    {
        var tracks = new List<BookTrack>(chapterTracks.Count + 2);
        if (TypedText(selection.Intro) is { Length: > 0 } intro)
        {
            tracks.Add(Track(EpubBookRules.TrackFileName(BookTrackKind.Intro), intro, null));
        }

        tracks.AddRange(chapterTracks
            .Where(track => track.ChapterNumber is { } number && !selection.Excluded.Contains(number))
            .OrderBy(track => track.ChapterNumber));
        if (TypedText(selection.Outro) is { Length: > 0 } outro)
        {
            tracks.Add(Track(EpubBookRules.TrackFileName(BookTrackKind.Outro), outro, null));
        }

        return tracks;
    }

    /// <summary>Intro and outro are read as typed: only horizontal whitespace runs collapse, and the ends are trimmed.</summary>
    private static string TypedText(string? text) =>
        HorizontalWhitespace().Replace((text ?? string.Empty).Replace("\r\n", "\n"), " ").Trim();

    private static BookTrack Track(string displayName, string text, int? chapterNumber) =>
        new(displayName, text, text.Trim().Length, Encoding.UTF8.GetByteCount(text), chapterNumber);

    [GeneratedRegex(@"[\p{Zs}\t\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespace();
}
