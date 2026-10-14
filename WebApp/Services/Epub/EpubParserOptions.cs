using PereneTts.Models;

// Ported from book-notes-ia/services/EbookParseService.Api/Options/EpubParserOptions.cs (no input/output folders).
namespace PereneTts.Services.Epub;

public sealed class EpubParserOptions
{
    public const string SectionName = "EpubParser";

    public long MaxSourceFileBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxArchiveEntryCount { get; set; } = 10_000;
    public long MaxEntryBytes { get; set; } = 20 * 1024 * 1024;
    public long MaxTotalUncompressedBytes { get; set; } = 500 * 1024 * 1024;
    public long MaxXmlCharacters { get; set; } = 25 * 1024 * 1024;
    /// <summary>Caps the parsed text a circuit keeps in memory.</summary>
    public long MaxBookCharacters { get; set; } = 20_000_000;
    /// <summary>Target size of a fallback part for books without usable chapters.</summary>
    public int FallbackPartCharacters { get; set; } = 30_000;

    public bool LimitsArePositive() =>
        MaxSourceFileBytes > 0 && MaxArchiveEntryCount > 0 && MaxEntryBytes > 0
        && MaxTotalUncompressedBytes > 0 && MaxXmlCharacters > 0 && MaxBookCharacters > 0
        && FallbackPartCharacters is > 0 and <= BatchRules.MaxCharacters;
}
