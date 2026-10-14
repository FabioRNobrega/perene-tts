namespace PereneTts.Models;

/// <summary>A strictly parsed EPUB: package title, package (or inferred) language, and chapters in book order.</summary>
public sealed record ParsedEpubBook(string Title, string Language, IReadOnlyList<ParsedChapter> Chapters);

/// <summary>One discovered chapter with its raw, whitespace-normalized paragraphs.</summary>
public sealed record ParsedChapter(int Number, IReadOnlyList<string> Paragraphs);

/// <summary>What the user adds or leaves out: typed intro/outro and the excluded chapter numbers.</summary>
public sealed record BookSelection(string Intro, string Outro, IReadOnlySet<int> Excluded);

/// <summary>One future batch track: upload name, exact UTF-8 text, trimmed characters, bytes, and its chapter (null for intro/outro).</summary>
public sealed record BookTrack(string DisplayName, string Text, int Characters, int Bytes, int? ChapterNumber);

public enum BookTrackKind { Intro, Chapter, Outro }
