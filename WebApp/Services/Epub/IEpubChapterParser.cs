using PereneTts.Models;

namespace PereneTts.Services.Epub;

public interface IEpubChapterParser
{
    /// <summary>Strictly discovers chapters from a seekable EPUB stream; failures are EpubParseException.</summary>
    Task<ParsedEpubBook> ParseAsync(Stream epub, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every spine XHTML document's narration text in reading order, with the same archive, XML, and
    /// extraction rules, but without chapter discovery. Used only for the user-chosen parts fallback.
    /// </summary>
    Task<EpubReadingText> ReadReadingOrderAsync(Stream epub, CancellationToken cancellationToken = default);
}

public sealed record EpubReadingText(string Title, string Language, IReadOnlyList<string> Paragraphs);
