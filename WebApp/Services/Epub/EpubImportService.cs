using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;

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
                return new EpubImportResult(await parser.ParseAsync(prepared.Stream, cancellation), prepared.Repaired);
            }, cancellation);

            logger.LogInformation(
                "EPUB import succeeded: {ChapterCount} chapters, {Characters} characters, repaired {Repaired}.",
                result.Book.Chapters.Count,
                result.Book.Chapters.Sum(chapter => chapter.Paragraphs.Sum(paragraph => (long)paragraph.Length)),
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
