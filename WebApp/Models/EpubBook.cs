namespace PereneTts.Models;

/// <summary>A parsed EPUB: package title, package (or inferred) language, and its narratable items in book order.</summary>
public sealed record ParsedEpubBook(
    string Title,
    string Language,
    IReadOnlyList<ParsedChapter> Chapters,
    BookStructure Structure = BookStructure.Chapters);

/// <summary>
/// One narratable item with whitespace-normalized paragraphs. Number is the chapter number, the part number,
/// or the section's ordinal; Title is the table-of-contents label for sections; Order is the 1-based book position.
/// </summary>
public sealed record ParsedChapter(
    int Number,
    IReadOnlyList<string> Paragraphs,
    BookItemKind Kind = BookItemKind.Chapter,
    string? Title = null,
    int Order = 0);

/// <summary>Chapters come from explicit structure; parts come from the user-chosen fixed-size fallback.</summary>
public enum BookStructure { Chapters, Parts }

public enum BookItemKind { Chapter, Section, Part }

/// <summary>What the user adds or leaves out: typed intro/outro and the excluded item Order values.</summary>
public sealed record BookSelection(string Intro, string Outro, IReadOnlySet<int> Excluded);

/// <summary>One future batch track: upload name, exact UTF-8 text, trimmed characters, bytes, and its book item (null for intro/outro).</summary>
public sealed record BookTrack(string DisplayName, string Text, int Characters, int Bytes, int? ItemOrder, BookItemKind? Kind = null);

public enum BookTrackKind { Intro, Chapter, Section, Part, Outro }
