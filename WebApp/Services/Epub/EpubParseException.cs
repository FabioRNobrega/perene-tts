// Ported from book-notes-ia/services/EbookParseService.Api/Services/EpubParseException.cs (OutputConflict removed).
namespace PereneTts.Services.Epub;

public enum EpubParseErrorKind
{
    InvalidRequest,
    InvalidEpub,
    UnsupportedStructure,
    UnsupportedContent
}

/// <summary>An import failure whose PublicMessage is safe to show; inner exceptions stay in logs.</summary>
public sealed class EpubParseException : Exception
{
    public EpubParseException(EpubParseErrorKind kind, string publicMessage, Exception? innerException = null)
        : base(publicMessage, innerException)
    {
        Kind = kind;
        PublicMessage = publicMessage;
    }

    public EpubParseErrorKind Kind { get; }
    public string PublicMessage { get; }
}
