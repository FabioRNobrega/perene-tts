using PereneTts.Models;

namespace PereneTts.Services.Epub;

public interface IBookTrackBuilder
{
    /// <summary>The exact narrated chapter text: spoken heading (when a language is known) and normalized paragraphs.</summary>
    string ChapterText(ParsedChapter chapter, string? languageId);

    BookTrack ChapterTrack(ParsedChapter chapter, string? languageId);

    /// <summary>Intro, included chapters in book order, then outro.</summary>
    IReadOnlyList<BookTrack> Build(ParsedEpubBook book, BookSelection selection, string? languageId);

    /// <summary>Same ordering over chapter tracks the caller already rendered (one per chapter).</summary>
    IReadOnlyList<BookTrack> Build(IReadOnlyList<BookTrack> chapterTracks, BookSelection selection);
}
