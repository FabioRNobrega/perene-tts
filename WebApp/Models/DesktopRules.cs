namespace PereneTts.Models;

public enum CloseKind { CloseNow, ConfirmStopBatches, ConfirmDiscardGeneration }

/// <summary>What closing the desktop window must ask first. Batches lists the conversions that would be stopped.</summary>
public sealed record CloseDecision(CloseKind Kind, IReadOnlyList<BatchSummary> Batches, bool GenerationRunning);

/// <summary>Pure close rules for the desktop window: active conversions and generations are never dropped silently.</summary>
public static class DesktopRules
{
    public const string KeepWorking = "Keep working";
    public const string TracksKept = "Completed tracks are kept. Use Retry later to convert the rest.";
    public const string GenerationLost = "The voice or audio being generated now will be lost.";

    /// <summary>Paused, stopping and finished batches are safe to leave; these would keep converting.</summary>
    public static bool BlocksClose(string state) => state is "queued" or "running" or "pausing";

    public static CloseDecision Decide(bool generationRunning, IEnumerable<BatchSummary> batches)
    {
        var blocking = batches.Where(batch => BlocksClose(batch.State)).ToList();
        var kind = blocking.Count > 0 ? CloseKind.ConfirmStopBatches
            : generationRunning ? CloseKind.ConfirmDiscardGeneration
            : CloseKind.CloseNow;
        return new(kind, blocking, generationRunning);
    }

    public static string Title(CloseDecision decision) =>
        decision.Kind == CloseKind.ConfirmStopBatches ? "Stop conversion and quit?" : "Discard and quit?";

    public static string ConfirmLabel(CloseDecision decision) =>
        decision.Kind == CloseKind.ConfirmStopBatches ? "Stop conversion and quit" : "Discard and quit";

    /// <summary>Plain-text explanation, also used by the host's native fallback question.</summary>
    public static string Message(CloseDecision decision)
    {
        var lines = new List<string>();
        if (decision.Kind == CloseKind.ConfirmStopBatches)
        {
            lines.Add($"Quitting stops {(decision.Batches.Count == 1 ? "this conversion" : "these conversions")}: " +
                string.Join(", ", decision.Batches.Select(batch => $"“{batch.Name}”")) + ".");
            lines.Add(TracksKept);
        }
        if (decision.GenerationRunning) lines.Add(GenerationLost);
        return string.Join(" ", lines);
    }
}
