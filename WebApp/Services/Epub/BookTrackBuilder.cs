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
        // Parts have no heading: a fixed-size cut can fall mid-scene.
        if (languageId is null || chapter.Kind == BookItemKind.Part)
        {
            return body;
        }

        if (chapter.Kind == BookItemKind.Section)
        {
            return $"{EpubBookRules.SectionHeading(chapter.Title ?? string.Empty)}\n\n{body}";
        }

        var number = numbers.TryConvert(chapter.Number, languageId, out var words)
            ? words
            : chapter.Number.ToString(CultureInfo.InvariantCulture);
        return $"{EpubBookRules.ChapterLabel(languageId)} {number}.\n\n{body}";
    }

    public IReadOnlyList<BookTrack> ItemTracks(ParsedEpubBook book, string? languageId)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        return book.Chapters.Select(item => Track(FileName(item, used), ChapterText(item, languageId), item.Order, item.Kind)).ToList();
    }

    public IReadOnlyList<BookTrack> Build(ParsedEpubBook book, BookSelection selection, string? languageId) =>
        Build(ItemTracks(book, languageId), selection);

    public IReadOnlyList<BookTrack> Build(IReadOnlyList<BookTrack> itemTracks, BookSelection selection)
    {
        var tracks = new List<BookTrack>(itemTracks.Count + 2);
        if (TypedText(selection.Intro) is { Length: > 0 } intro)
        {
            tracks.Add(Track(EpubBookRules.TrackFileName(BookTrackKind.Intro), intro, null, null));
        }

        tracks.AddRange(itemTracks
            .Where(track => track.ItemOrder is { } order && !selection.Excluded.Contains(order))
            .OrderBy(track => track.ItemOrder));
        if (TypedText(selection.Outro) is { Length: > 0 } outro)
        {
            tracks.Add(Track(EpubBookRules.TrackFileName(BookTrackKind.Outro), outro, null, null));
        }

        return tracks;
    }

    private static string FileName(ParsedChapter item, ISet<string> used) => item.Kind switch
    {
        BookItemKind.Section => EpubBookRules.SectionFileName(item.Title ?? string.Empty, item.Number, used),
        BookItemKind.Part => EpubBookRules.TrackFileName(BookTrackKind.Part, item.Number),
        _ => EpubBookRules.TrackFileName(BookTrackKind.Chapter, item.Number)
    };

    /// <summary>Intro and outro are read as typed: only horizontal whitespace runs collapse, and the ends are trimmed.</summary>
    private static string TypedText(string? text) =>
        HorizontalWhitespace().Replace((text ?? string.Empty).Replace("\r\n", "\n"), " ").Trim();

    private static BookTrack Track(string displayName, string text, int? itemOrder, BookItemKind? kind) =>
        new(displayName, text, text.Trim().Length, Encoding.UTF8.GetByteCount(text), itemOrder, kind);

    [GeneratedRegex(@"[\p{Zs}\t\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespace();
}
