namespace PereneTts.Services.Epub;

public interface ITtsTextNormalizer
{
    /// <summary>Rewrites paragraphs for narration in en/pt/sv; a null language applies only language-neutral rules.</summary>
    IReadOnlyList<string> NormalizeParagraphs(IReadOnlyList<string> paragraphs, string? languageId);
}
