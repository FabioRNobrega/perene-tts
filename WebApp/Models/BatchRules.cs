using System.Text;
namespace PereneTts.Models;

/// <summary>Pure batch presentation rules: narration order, naming, limits, which actions apply, progress, and My creations links.</summary>
public static class BatchRules
{
    public const int MaxFiles = 200;
    public const int MaxFileBytes = 1024 * 1024;
    public const long MaxTotalBytes = 10 * 1024 * 1024;
    public const int MaxCharacters = 200_000;
    public const int MaxNameLength = 80;

    /// <summary>Intro first, then chapter-NNN, then other files, then outro; each group in natural filename order.</summary>
    public static IReadOnlyList<string> DefaultOrder(IEnumerable<string> names) => DefaultOrder(names, name => name);

    public static IReadOnlyList<T> DefaultOrder<T>(IEnumerable<T> items, Func<T, string> name) =>
        items.OrderBy(item => OrderGroup(name(item))).ThenBy(name, NaturalComparer.Instance).ToList();

    private static int OrderGroup(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.EndsWith("intro", StringComparison.OrdinalIgnoreCase)) return 0;
        if (stem.EndsWith("outro", StringComparison.OrdinalIgnoreCase)) return 3;
        return stem.StartsWith("chapter-", StringComparison.OrdinalIgnoreCase) && stem.Length > 8 && stem[8..].All(char.IsAsciiDigit) ? 1 : 2;
    }

    public static string OutputName(string batchName, int number) => $"{batchName.Trim()} {number:000}.mp3";

    public static bool IsTextFile(string name) => name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    public static string? NameError(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "Enter a batch name.";
        if (trimmed.Length > MaxNameLength) return "Batch name must be 80 characters or fewer.";
        if (trimmed.Any(character => character is '/' or '\\' || char.IsControl(character)))
            return "Batch name cannot contain /, \\, or control characters.";
        return null;
    }

    /// <summary>Per-file pre-check mirroring the worker; characters is null when the file is not valid UTF-8.</summary>
    public static string? FileError(string name, long size, int? characters) =>
        size > MaxFileBytes ? $"{name}: file exceeds 1 MiB"
        : characters is null ? $"{name}: file is not valid UTF-8 text"
        : characters == 0 ? $"{name}: file is empty"
        : characters > MaxCharacters ? $"{name}: file exceeds 200,000 characters"
        : null;

    public static string? SelectionError(int count, long totalBytes) =>
        count > MaxFiles ? $"A batch may contain at most {MaxFiles} files."
        : totalBytes > MaxTotalBytes ? "The batch exceeds 10 MiB in total."
        : null;

    /// <summary>Counts characters after trimming surrounding whitespace, rejecting invalid UTF-8; a leading BOM is ignored.</summary>
    public static async Task<int?> MeasureTextAsync(Stream stream, CancellationToken cancellation)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 16384, leaveOpen: true);
        var buffer = new char[8192];
        long index = 0, first = -1, last = -1;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
            {
                for (var i = 0; i < read; i++, index++)
                {
                    if (index == 0 && buffer[i] == '﻿') continue;
                    if (char.IsWhiteSpace(buffer[i])) continue;
                    if (first < 0) first = index;
                    last = index;
                }
            }
        }
        catch (DecoderFallbackException) { return null; }
        return first < 0 ? 0 : (int)Math.Min(int.MaxValue, last - first + 1);
    }

    public static bool CanPause(string state) => state is "queued" or "running";
    public static bool CanResume(string state) => state is "paused";
    public static bool CanStop(string state) => state is "queued" or "running" or "pausing" or "paused";
    public static bool CanRetry(string state) => state is "failed" or "stopped";
    public static bool CanDelete(string state) => state is not ("running" or "pausing" or "stopping");
    public static bool CanRetryTrack(string trackState) => trackState is "failed";
    public static bool IsActive(string state) => state is "queued" or "running" or "pausing" or "stopping";
    public static bool Animates(string state) => state is "running" or "pausing" or "stopping";

    public static string StateLabel(string state) => state.Length == 0 ? state : char.ToUpperInvariant(state[0]) + state[1..];

    /// <summary>One colour per state family; batch states and track states share it.</summary>
    public static string BadgeClass(string state) => "state-badge " + state switch
    {
        "running" => "state-running",
        "queued" or "pending" => "state-waiting",
        "pausing" or "paused" => "state-paused",
        "stopping" or "stopped" => "state-stopped",
        "completed" => "state-success",
        "failed" => "state-danger",
        _ => "state-waiting"
    };

    /// <summary>Overall percent by completed chunks across all tracks.</summary>
    public static int Percent(BatchSummary batch) => batch.State == "completed" ? 100 : Percent(batch.ChunksDone, batch.ChunkCount);

    public static int Percent(int done, int total) => total <= 0 ? 0 : Math.Clamp((int)(done * 100L / total), 0, 100);

    /// <summary>Exact overall percent for the bar width; a long book moves well under 1% per chunk.</summary>
    public static double ExactPercent(BatchSummary batch) =>
        batch.State == "completed" ? 100 : batch.ChunkCount <= 0 ? 0 : Math.Clamp(batch.ChunksDone * 100d / batch.ChunkCount, 0, 100);

    /// <summary>Bar width: any real progress stays visible as a sliver instead of an empty track.</summary>
    public static double BarWidth(BatchSummary batch) =>
        ExactPercent(batch) is var exact && exact > 0 ? Math.Max(exact, 1.5) : 0;

    /// <summary>One decimal below 10% so early progress does not read as 0%.</summary>
    public static string PercentLabel(BatchSummary batch) => ExactPercent(batch) switch
    {
        0 => "0%",
        < 10 and var exact => $"{Math.Max(0.1, Math.Floor(exact * 10) / 10).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%",
        var exact => $"{(int)exact}%"
    };

    public static TimeSpan Elapsed(BatchSummary batch) => TimeSpan.FromSeconds(Math.Max(0, batch.ChunkSecondsTotal));

    public static TimeSpan? Remaining(BatchSummary batch) =>
        Remaining(batch.ChunkCount, batch.ChunksDone, batch.ChunkSecondsTotal, batch.ChunksDoneTotal);

    /// <summary>Remaining chunks times the measured average chunk time; null without timing data.</summary>
    public static TimeSpan? Remaining(int chunkCount, int chunksDone, double secondsTotal, int timedChunks) =>
        timedChunks <= 0 || secondsTotal <= 0 ? null
        : TimeSpan.FromSeconds(Math.Max(0, chunkCount - chunksDone) * secondsTotal / timedChunks);

    public static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes}:{value.Seconds:00}";

    /// <summary>The batch the sidebar summarizes: the one converting, otherwise the next queued.</summary>
    public static BatchSummary? ActiveBatch(IEnumerable<BatchSummary> batches)
    {
        var list = batches.ToList();
        return list.FirstOrDefault(batch => batch.State is "running" or "pausing" or "stopping")
            ?? list.Where(batch => batch.State == "queued").MinBy(batch => batch.CreatedAt);
    }

    public static string SidebarStatus(BatchSummary batch) => $"Batch · {batch.TracksCompleted}/{batch.TrackCount} tracks";

    public static string TrackUrl(string batchId, int number, bool download = false) =>
        $"/batches/{batchId}/tracks/{number}{(download ? "?download=1" : "")}";

    public static string ArchiveUrl(string batchId) => $"/batches/{batchId}/archive";

    public static string OpenBatchUrl(string batchId) => $"/batch-audio?batch={batchId}";

    public static bool IsBatchTrack(Creation creation) => creation.BatchId is not null && creation.TrackNumber is not null;

    public static string AudioUrl(Creation creation, bool download = false) =>
        IsBatchTrack(creation) ? TrackUrl(creation.BatchId!, creation.TrackNumber!.Value, download)
        : $"/audio/{creation.JobId}{(download ? "?download=1" : "")}";

    /// <summary>My creations type chip: label (shown uppercase), icon, and colour class per kind.</summary>
    public static (string Label, string Icon, string CssClass) CreationType(Creation creation) =>
        IsBatchTrack(creation) ? ("Batch track", "bi-files", "type-batch")
        : creation.Kind == "preview" ? ("Voice test", "bi-mic", "type-voice")
        : ("Audio", "bi-soundwave", "type-audio");

    public static string TagLabel(Creation creation) =>
        $"Batch · {creation.BatchName} · {creation.TrackNumber:000}/{creation.TrackCount:000}";

    public static bool MatchesBatch(Creation creation, string? batchFilter) =>
        string.IsNullOrEmpty(batchFilter) || creation.BatchId == batchFilter;
}

/// <summary>Case-insensitive ordering that compares digit runs by value, so chapter-2 sorts before chapter-10.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= "";
        y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int startX = i, startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var left = x[startX..i].TrimStart('0');
                var right = y[startY..j].TrimStart('0');
                var compared = left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
                if (compared != 0) return compared;
            }
            else
            {
                var compared = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (compared != 0) return compared;
                i++;
                j++;
            }
        }
        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
