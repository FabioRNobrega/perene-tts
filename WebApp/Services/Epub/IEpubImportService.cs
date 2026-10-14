using Microsoft.AspNetCore.Components.Forms;
using PereneTts.Models;

namespace PereneTts.Services.Epub;

/// <summary>Exactly one of Book (strict discovery succeeded) and Fallback (no usable chapters) is set.</summary>
public sealed record EpubImportResult(ParsedEpubBook? Book, bool Repaired, EpubFallbackOffer? Fallback = null);

/// <summary>The strict failure reason plus a fixed-size parts book the user may choose instead.</summary>
public sealed record EpubFallbackOffer(string Reason, ParsedEpubBook Parts);

public interface IEpubImportService
{
    /// <summary>
    /// Streams the browser file to a private temporary file, repairs its packaging if needed, and parses it.
    /// When chapter structure is unsupported, returns a parts fallback offer instead of throwing.
    /// </summary>
    Task<EpubImportResult> ImportAsync(IBrowserFile file, CancellationToken cancellation);
}
