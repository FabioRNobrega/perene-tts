using PereneTts.Models;

namespace PereneTts.Services.Epub;

public interface IBookTrackBuilder
{
    /// <summary>The exact narrated text of a book item: its spoken heading (chapters and sections, once a language is known) and normalized paragraphs.</summary>
    string ChapterText(ParsedChapter chapter, string? languageId);

    /// <summary>One track per book item, in book order, with unique upload names.</summary>
    IReadOnlyList<BookTrack> ItemTracks(ParsedEpubBook book, string? languageId);

    /// <summary>Intro, included items in book order, then outro.</summary>
    IReadOnlyList<BookTrack> Build(ParsedEpubBook book, BookSelection selection, string? languageId);

    /// <summary>Same ordering over item tracks the caller already rendered with <see cref="ItemTracks"/>.</summary>
    IReadOnlyList<BookTrack> Build(IReadOnlyList<BookTrack> itemTracks, BookSelection selection);
}
