using PereneTts.Desktop;

namespace WebApp.Tests;

public sealed class WorkerReadyLineTests
{
    [Theory]
    [InlineData("PERENE_WORKER_READY port=41873", 41873)]
    [InlineData("PERENE_WORKER_READY port=1", 1)]
    [InlineData("PERENE_WORKER_READY port=65535", 65535)]
    public void AcceptsTheReadyLine(string line, int expected)
    {
        Assert.True(WorkerReadyLine.TryParse(line, out var port));
        Assert.Equal(expected, port);
    }

    [Theory]
    [InlineData("PERENE_WORKER_READY port=0")]
    [InlineData("PERENE_WORKER_READY port=65536")]
    [InlineData("PERENE_WORKER_READY port=41873 extra")]
    [InlineData(" PERENE_WORKER_READY port=41873")]
    [InlineData("PERENE_WORKER_READY port=")]
    [InlineData("OTHER_READY port=41873")]
    [InlineData("INFO: Uvicorn running on http://127.0.0.1:41873")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsAnythingElse(string? line)
    {
        Assert.False(WorkerReadyLine.TryParse(line, out var port));
        Assert.Equal(0, port);
    }
}
