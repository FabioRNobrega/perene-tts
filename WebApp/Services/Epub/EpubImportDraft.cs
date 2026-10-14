using PereneTts.Models;

namespace PereneTts.Services.Epub;

// One draft per Blazor circuit keeps an imported book across tab switches and navigation; a reload starts empty.
// Batch conversion state never lives here; the worker owns it.
public sealed class EpubImportDraft
{
    public ParsedEpubBook? Book { get; private set; }
    public bool Repaired { get; private set; }
    /// <summary>The browser file name, used only as a display label.</summary>
    public string SourceLabel { get; private set; } = "";
    public string Name { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string Intro { get; set; } = "";
    public string Outro { get; set; } = "";
    public HashSet<int> Excluded { get; } = [];
    public HashSet<int> Expanded { get; } = [];

    /// <summary>Replaces the current import with a successfully parsed book; the chosen voice is kept.</summary>
    public void Load(EpubImportResult result, string sourceLabel)
    {
        Book = result.Book;
        Repaired = result.Repaired;
        SourceLabel = sourceLabel;
        Name = EpubBookRules.DefaultBatchName(result.Book.Title);
        Intro = "";
        Outro = "";
        Excluded.Clear();
        Expanded.Clear();
    }

    public void Clear()
    {
        Book = null;
        Repaired = false;
        SourceLabel = "";
        Name = "";
        VoiceId = "";
        Intro = "";
        Outro = "";
        Excluded.Clear();
        Expanded.Clear();
    }
}
