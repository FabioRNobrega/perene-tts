using Microsoft.AspNetCore.Components.Forms;
using PereneTts.Models;

namespace PereneTts.Services.Epub;

public sealed record EpubImportResult(ParsedEpubBook Book, bool Repaired);

public interface IEpubImportService
{
    /// <summary>Streams the browser file to a private temporary file, repairs its packaging if needed, and parses it.</summary>
    Task<EpubImportResult> ImportAsync(IBrowserFile file, CancellationToken cancellation);
}
