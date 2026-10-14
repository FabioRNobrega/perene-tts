using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;
using PereneTts.Models;

namespace PereneTts.Services.Epub;

// Microsoft Learn (Blazor file uploads, .NET 10): pass an explicit maxAllowedSize, stream server-side uploads to disk
// rather than memory, and never build a path from the client file name.
public sealed class EpubImportService(
    IEpubArchiveRepairer repairer,
    IEpubChapterParser parser,
    IOptions<EpubParserOptions> options,
    ILogger<EpubImportService> logger) : IEpubImportService
{
    private readonly EpubParserOptions _options = options.Value;

    public async Task<EpubImportResult> ImportAsync(IBrowserFile file, CancellationToken cancellation)
    {
        bool? repaired = null;
        try
        {
            await using var temp = EpubTempFile.Create();
            try
            {
                await using var upload = file.OpenReadStream(_options.MaxSourceFileBytes, cancellation);
                await upload.CopyToAsync(temp, cancellation);
            }
            catch (IOException exception) when (!cancellation.IsCancellationRequested)
            {
                throw new EpubParseException(
                    EpubParseErrorKind.InvalidRequest,
                    file.Size > _options.MaxSourceFileBytes
                        ? $"The EPUB exceeds {_options.MaxSourceFileBytes / (1024 * 1024)} MiB."
                        : "The EPUB could not be uploaded. Try again.",
                    exception);
            }

            temp.Position = 0;
            // Repair and parse are synchronous CPU/IO work; keep them off the circuit's synchronization context.
            var result = await Task.Run(async () =>
            {
                await using var prepared = await repairer.PrepareAsync(temp, cancellation);
                repaired = prepared.Repaired;
                try
                {
                    return new EpubImportResult(await parser.ParseAsync(prepared.Stream, cancellation), prepared.Repaired);
                }
                catch (EpubParseException strict) when (strict.Kind == EpubParseErrorKind.UnsupportedStructure)
                {
                    // Only a chapter-structure failure offers parts; damaged, encrypted, or oversized books still fail.
                    var text = await parser.ReadReadingOrderAsync(prepared.Stream, cancellation);
                    var parts = EpubBookRules.SplitIntoParts(text.Paragraphs, _options.FallbackPartCharacters);
                    if (parts.Count == 0)
                    {
                        throw new EpubParseException(
                            EpubParseErrorKind.UnsupportedStructure, $"{strict.PublicMessage} This book has no readable text.", strict);
                    }

                    var book = new ParsedEpubBook(
                        text.Title,
                        text.Language,
                        parts.Select((paragraphs, index) => new ParsedChapter(index + 1, paragraphs, BookItemKind.Part, null, index + 1)).ToList(),
                        BookStructure.Parts);
                    return new EpubImportResult(null, prepared.Repaired, new EpubFallbackOffer(strict.PublicMessage, book));
                }
            }, cancellation);

            var items = (result.Book ?? result.Fallback!.Parts).Chapters;
            logger.LogInformation(
                "EPUB import finished: {Source}, {Chapters} chapters, {Sections} sections, {Parts} parts, {Characters} characters, repaired {Repaired}.",
                result.Book is null ? "fallback offer" : "chapters",
                items.Count(item => item.Kind == BookItemKind.Chapter),
                items.Count(item => item.Kind == BookItemKind.Section),
                items.Count(item => item.Kind == BookItemKind.Part),
                items.Sum(item => item.Paragraphs.Sum(paragraph => (long)paragraph.Length)),
                result.Repaired);
            return result;
        }
        catch (EpubParseException exception)
        {
            // Only the category and repair flag: never the title, text, or client file name.
            logger.LogWarning("EPUB import failed: {ErrorKind}, repaired {Repaired}.", exception.Kind, repaired);
            throw;
        }
    }
}
