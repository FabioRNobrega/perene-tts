using System.Text;
using PereneTts.Models;

namespace WebApp.Tests;

public sealed class BatchRulesTests
{
    private static readonly string[] AllStates = ["queued", "running", "pausing", "paused", "stopping", "stopped", "failed", "completed"];

    [Fact]
    public void DefaultOrderPlacesIntroChaptersAndOutroForTheExampleFolder()
    {
        var chapters = Enumerable.Range(1, 16).Select(number => $"chapter-{number:000}.txt").ToList();
        var shuffled = chapters.AsEnumerable().Reverse()
            .Prepend("InMiltonLumkyTerritoryOutro.txt").Append("InMiltonLumkyTerritoryIntro.txt").ToList();

        var ordered = BatchRules.DefaultOrder(shuffled);

        Assert.Equal(["InMiltonLumkyTerritoryIntro.txt", .. chapters, "InMiltonLumkyTerritoryOutro.txt"], ordered);
    }

    [Fact]
    public void IntroAndOutroAreCaseInsensitive()
    {
        Assert.Equal(["Book-Intro.TXT", "chapter-1.txt", "BOOKOUTRO.txt"],
            BatchRules.DefaultOrder(["BOOKOUTRO.txt", "chapter-1.txt", "Book-Intro.TXT"]));
    }

    [Fact]
    public void ChaptersUseNaturalNumericOrder()
    {
        Assert.Equal(["chapter-2.txt", "chapter-10.txt"], BatchRules.DefaultOrder(["chapter-10.txt", "chapter-2.txt"]));
    }

    [Fact]
    public void UnknownNamesLandBetweenChaptersAndOutro()
    {
        Assert.Equal(["intro.txt", "chapter-001.txt", "appendix 2.txt", "appendix 10.txt", "outro.txt"],
            BatchRules.DefaultOrder(["outro.txt", "appendix 10.txt", "chapter-001.txt", "appendix 2.txt", "intro.txt"]));
    }

    [Fact]
    public void OutputNamesAreZeroPadded()
    {
        Assert.Equal("Lumky 007.mp3", BatchRules.OutputName("Lumky", 7));
        Assert.Equal("Lumky 018.mp3", BatchRules.OutputName(" Lumky ", 18));
    }

    [Theory]
    [InlineData("Lumky", null)]
    [InlineData("   ", "Enter a batch name.")]
    [InlineData("a/b", "Batch name cannot contain /, \\, or control characters.")]
    [InlineData("a\\b", "Batch name cannot contain /, \\, or control characters.")]
    [InlineData("tab\tname", "Batch name cannot contain /, \\, or control characters.")]
    public void NameRules(string name, string? expected) => Assert.Equal(expected, BatchRules.NameError(name));

    [Fact]
    public void NameLongerThanEightyCharactersIsRejected()
    {
        Assert.Null(BatchRules.NameError(new string('x', 80)));
        Assert.NotNull(BatchRules.NameError(new string('x', 81)));
    }

    [Theory]
    [InlineData(10, 5, null)]
    [InlineData(BatchRules.MaxFileBytes + 1, 5, "a.txt: file exceeds 1 MiB")]
    [InlineData(10, null, "a.txt: file is not valid UTF-8 text")]
    [InlineData(10, 0, "a.txt: file is empty")]
    [InlineData(10, BatchRules.MaxCharacters + 1, "a.txt: file exceeds 200,000 characters")]
    public void FileRules(long size, int? characters, string? expected) => Assert.Equal(expected, BatchRules.FileError("a.txt", size, characters));

    [Fact]
    public void SelectionRules()
    {
        Assert.Null(BatchRules.SelectionError(200, BatchRules.MaxTotalBytes));
        Assert.NotNull(BatchRules.SelectionError(201, 10));
        Assert.NotNull(BatchRules.SelectionError(2, BatchRules.MaxTotalBytes + 1));
    }

    [Fact]
    public async Task MeasureTextTrimsWhitespaceIgnoresBomAndRejectsInvalidUtf8()
    {
        Assert.Equal(4, await Measure([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("  Olá!\n\n")]));
        Assert.Equal(0, await Measure(Encoding.UTF8.GetBytes(" \n\t ")));
        Assert.Null(await Measure([0xFF, 0xFE, 0x41]));
    }

    private static Task<int?> Measure(byte[] content) => BatchRules.MeasureTextAsync(new MemoryStream(content), CancellationToken.None);

    public static TheoryData<string> States() => new(AllStates);

    [Theory]
    [MemberData(nameof(States))]
    public void ActionRulesCoverEveryState(string state)
    {
        Assert.Equal(state is "queued" or "running", BatchRules.CanPause(state));
        Assert.Equal(state == "paused", BatchRules.CanResume(state));
        Assert.Equal(state is "queued" or "running" or "pausing" or "paused", BatchRules.CanStop(state));
        Assert.Equal(state is "failed" or "stopped", BatchRules.CanRetry(state));
        Assert.Equal(state is not ("running" or "pausing" or "stopping"), BatchRules.CanDelete(state));
    }

    [Fact]
    public void TrackRetryOnlyForFailedTracks()
    {
        Assert.True(BatchRules.CanRetryTrack("failed"));
        Assert.False(BatchRules.CanRetryTrack("completed"));
        Assert.False(BatchRules.CanRetryTrack("pending"));
    }

    [Fact]
    public void PercentAndRemaining()
    {
        Assert.Equal(0, BatchRules.Percent(0, 0));
        Assert.Equal(33, BatchRules.Percent(1, 3));
        Assert.Equal(100, BatchRules.Percent(Summary("completed", chunkCount: 10, chunksDone: 3)));
        Assert.Equal(30, BatchRules.Percent(Summary("running", chunkCount: 10, chunksDone: 3)));
        Assert.Null(BatchRules.Remaining(10, 0, 0, 0));
        Assert.Equal(TimeSpan.FromSeconds(140), BatchRules.Remaining(10, 3, 60, 3));
        Assert.Equal(TimeSpan.FromMinutes(2), BatchRules.Remaining(Summary("running", chunkCount: 10, chunksDone: 6, seconds: 120, timed: 4)));
        Assert.Equal("1:02:05", BatchRules.FormatDuration(TimeSpan.FromSeconds(3725)));
        Assert.Equal("2:05", BatchRules.FormatDuration(TimeSpan.FromSeconds(125)));
    }

    [Theory]
    [InlineData("running", "state-running")]
    [InlineData("queued", "state-waiting")]
    [InlineData("pending", "state-waiting")]
    [InlineData("pausing", "state-paused")]
    [InlineData("paused", "state-paused")]
    [InlineData("stopping", "state-stopped")]
    [InlineData("stopped", "state-stopped")]
    [InlineData("completed", "state-success")]
    [InlineData("failed", "state-danger")]
    public void EachStateFamilyHasItsOwnBadgeColour(string state, string expected) =>
        Assert.Equal($"state-badge {expected}", BatchRules.BadgeClass(state));

    [Fact]
    public void EarlyProgressStaysVisible()
    {
        Assert.Equal("0%", BatchRules.PercentLabel(Summary("queued", chunkCount: 2000, chunksDone: 0)));
        Assert.Equal(0, BatchRules.BarWidth(Summary("queued", chunkCount: 2000, chunksDone: 0)));
        var early = Summary("running", chunkCount: 2000, chunksDone: 3);
        Assert.Equal("0.1%", BatchRules.PercentLabel(early));
        Assert.Equal(1.5, BatchRules.BarWidth(early));
        Assert.Equal("4.2%", BatchRules.PercentLabel(Summary("running", chunkCount: 1000, chunksDone: 42)));
        Assert.Equal("42%", BatchRules.PercentLabel(Summary("running", chunkCount: 1000, chunksDone: 425)));
        Assert.Equal("100%", BatchRules.PercentLabel(Summary("completed", chunkCount: 10, chunksDone: 3)));
        Assert.Equal(42.5, BatchRules.BarWidth(Summary("running", chunkCount: 1000, chunksDone: 425)));
    }

    [Fact]
    public void CreationTypesAreDistinct()
    {
        var speech = new Creation("job-1", "speech", null, "Reader", "en", "Hi", DateTimeOffset.UnixEpoch);
        var preview = speech with { Kind = "preview" };
        Assert.Equal(("Audio", "bi-soundwave", "type-audio"), BatchRules.CreationType(speech));
        Assert.Equal(("Voice test", "bi-mic", "type-voice"), BatchRules.CreationType(preview));
        Assert.Equal(("Batch track", "bi-files", "type-batch"), BatchRules.CreationType(BatchTrack(Guid.NewGuid().ToString())));
    }

    [Fact]
    public void ActiveBatchPrefersTheConvertingBatch()
    {
        var older = Summary("queued", created: 1);
        var newer = Summary("queued", created: 2);
        Assert.Null(BatchRules.ActiveBatch([Summary("paused"), Summary("completed")]));
        Assert.Same(older, BatchRules.ActiveBatch([newer, older]));
        var running = Summary("pausing", created: 0);
        Assert.Same(running, BatchRules.ActiveBatch([newer, older, running]));
        Assert.Equal("Batch · 4/18 tracks", BatchRules.SidebarStatus(Summary("running", completed: 4, tracks: 18)));
    }

    [Fact]
    public void AudioUrlsForBatchAndOtherCreations()
    {
        var batchId = Guid.NewGuid().ToString();
        var track = BatchTrack(batchId);
        var speech = new Creation("job-1", "speech", null, "Reader", "en", "Hi", DateTimeOffset.UnixEpoch);

        Assert.Equal($"/batches/{batchId}/tracks/3", BatchRules.AudioUrl(track));
        Assert.Equal($"/batches/{batchId}/tracks/3?download=1", BatchRules.AudioUrl(track, download: true));
        Assert.Equal("/audio/job-1", BatchRules.AudioUrl(speech));
        Assert.Equal("/audio/job-1?download=1", BatchRules.AudioUrl(speech, download: true));
        Assert.Equal($"/batch-audio?batch={batchId}", BatchRules.OpenBatchUrl(batchId));
    }

    [Fact]
    public void BatchFilterAndTagLabel()
    {
        var batchId = Guid.NewGuid().ToString();
        var track = BatchTrack(batchId);
        var other = BatchTrack(Guid.NewGuid().ToString());
        var speech = new Creation("job-1", "speech", null, "Reader", "en", "Hi", DateTimeOffset.UnixEpoch);

        Assert.Equal("Batch · Lumky · 003/018", BatchRules.TagLabel(track));
        Assert.True(BatchRules.MatchesBatch(speech, null));
        Assert.True(BatchRules.MatchesBatch(track, batchId));
        Assert.False(BatchRules.MatchesBatch(other, batchId));
        Assert.False(BatchRules.MatchesBatch(speech, batchId));
    }

    private static Creation BatchTrack(string batchId) =>
        new($"{batchId}-003", "batch", null, "Reader", "en", "", DateTimeOffset.UnixEpoch, batchId, "Lumky", 3, 18, "chapter-002.txt");

    private static BatchSummary Summary(string state, int chunkCount = 0, int chunksDone = 0, double seconds = 0, int timed = 0,
        int created = 0, int completed = 0, int tracks = 1) =>
        new(Guid.NewGuid().ToString(), "Lumky", Guid.NewGuid().ToString(), "Reader", "en", state, null,
            DateTimeOffset.UnixEpoch.AddDays(created), DateTimeOffset.UnixEpoch, tracks, completed, 0, chunkCount, chunksDone,
            seconds, timed, null, null, null, null);
}
