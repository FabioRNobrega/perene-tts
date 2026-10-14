using PereneTts.Models;

namespace PereneTts.Services.Epub;

public interface IEpubChapterParser
{
    /// <summary>Strictly discovers chapters from a seekable EPUB stream; failures are EpubParseException.</summary>
    Task<ParsedEpubBook> ParseAsync(Stream epub, CancellationToken cancellationToken = default);
}
