using PereneTts.Models;

namespace WebApp.Tests;

public sealed class DesktopRulesTests
{
    private static BatchSummary Batch(string state, string name = "Lumky") =>
        new(Guid.NewGuid().ToString(), name, Guid.NewGuid().ToString(), "Reader", "en", state, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3, 0, 0, 0, 0, 0, 0, null, null, null, null);

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("pausing")]
    public void ConvertingBatchesMustBeStoppedBeforeQuitting(string state)
    {
        var blocking = Batch(state, "Book");
        var decision = DesktopRules.Decide(false, [blocking, Batch("paused"), Batch("completed")]);

        Assert.Equal(CloseKind.ConfirmStopBatches, decision.Kind);
        Assert.Equal([blocking], decision.Batches);
        Assert.Equal("Stop conversion and quit", DesktopRules.ConfirmLabel(decision));
        Assert.Contains("“Book”", DesktopRules.Message(decision));
        Assert.Contains("Completed tracks are kept", DesktopRules.Message(decision));
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("stopping")]
    [InlineData("stopped")]
    [InlineData("completed")]
    [InlineData("failed")]
    public void SettledBatchesCloseAtOnce(string state) =>
        Assert.Equal(CloseKind.CloseNow, DesktopRules.Decide(false, [Batch(state)]).Kind);

    [Fact]
    public void NothingActiveClosesAtOnce() =>
        Assert.Equal(CloseKind.CloseNow, DesktopRules.Decide(false, []).Kind);

    [Fact]
    public void RunningGenerationAsksToDiscard()
    {
        var decision = DesktopRules.Decide(true, [Batch("paused")]);

        Assert.Equal(CloseKind.ConfirmDiscardGeneration, decision.Kind);
        Assert.Empty(decision.Batches);
        Assert.Equal("Discard and quit", DesktopRules.ConfirmLabel(decision));
        Assert.Equal(DesktopRules.GenerationLost, DesktopRules.Message(decision));
    }

    [Fact]
    public void BatchesAndGenerationUseStopWordingThatMentionsTheGeneration()
    {
        var decision = DesktopRules.Decide(true, [Batch("running", "One"), Batch("queued", "Two")]);

        Assert.Equal(CloseKind.ConfirmStopBatches, decision.Kind);
        Assert.Equal(2, decision.Batches.Count);
        Assert.Equal("Stop conversion and quit", DesktopRules.ConfirmLabel(decision));
        var message = DesktopRules.Message(decision);
        Assert.Contains("these conversions", message);
        Assert.Contains("“One”, “Two”", message);
        Assert.Contains(DesktopRules.GenerationLost, message);
    }
}
