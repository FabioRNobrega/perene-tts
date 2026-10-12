using System.Net;
using PereneTts.Services;

namespace WebApp.Tests;

public sealed class AudioExportTests : IDisposable
{
    private static readonly Guid Id = Guid.Parse("6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f");
    private readonly string folder = Directory.CreateTempSubdirectory("perene-export-").FullName;

    [Theory]
    [InlineData("/audio/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f?download=1", "/audio/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f")]
    [InlineData("/batches/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f/tracks/3?download=1", "/batches/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f/tracks/3/audio")]
    [InlineData("/batches/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f/archive", "/batches/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f/archive")]
    public async Task EachStudioRouteCallsTheMatchingWorkerRoute(string href, string workerPath)
    {
        var (export, handler, shell, studio) = Create("perene.mp3");

        Assert.True(await export.SaveAsync(href, "perene.mp3"));

        Assert.Equal(workerPath, handler.Paths.Single());
        Assert.Equal("perene.mp3", shell.SuggestedNames.Single());
        Assert.Equal("Saved perene.mp3", studio.Notice);
    }

    [Theory]
    [InlineData("/audio/not-a-guid")]
    [InlineData("https://example.com/audio/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f")]
    [InlineData("/voices")]
    [InlineData("/batches/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f/tracks/3/../../archive")]
    public async Task UnknownLinksAreRejectedWithoutCallingTheWorker(string href)
    {
        var (export, handler, shell, studio) = Create("x.mp3");

        Assert.False(await export.SaveAsync(href, "x.mp3"));

        Assert.Empty(handler.Paths);
        Assert.Empty(shell.SuggestedNames);
        Assert.NotNull(studio.Error);
    }

    [Fact]
    public async Task CancelledDialogMakesNoRequestAndWritesNothing()
    {
        var (export, handler, _, studio) = Create(null);

        Assert.False(await export.SaveAsync($"/audio/{Id}", "perene.mp3"));

        Assert.Empty(handler.Paths);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.Null(studio.Error);
        Assert.Null(studio.Notice);
    }

    [Fact]
    public async Task SuccessWritesExactBytesAndLeavesNoPartialFile()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
        var (export, _, _, _) = Create("Book 003.mp3", bytes);

        Assert.True(await export.SaveAsync($"/batches/{Id}/tracks/3?download=1", "Book 003.mp3"));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder, "Book 003.mp3")));
        Assert.False(File.Exists(Path.Combine(folder, "Book 003.mp3.part")));
    }

    [Fact]
    public async Task ExistingFileIsReplaced()
    {
        var target = Path.Combine(folder, "Book.zip");
        await File.WriteAllTextAsync(target, "old contents that are longer than the new ones");
        var (export, _, shell, _) = Create("Book.zip", "new"u8.ToArray());

        Assert.True(await export.SaveAsync($"/batches/{Id}/archive", "Book.zip"));

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Equal(FileFilter.Zip, shell.Filters.Single());
    }

    [Fact]
    public async Task MidStreamFailureDeletesThePartialFileAndReportsAnError()
    {
        var (export, _, _, studio) = Create("perene.mp3", failAfterBytes: 4096);

        Assert.False(await export.SaveAsync($"/audio/{Id}", "perene.mp3"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.Contains("Could not save perene.mp3", studio.Error);
        Assert.Null(studio.Notice);
    }

    [Fact]
    public async Task WorkerErrorStatusWritesNothing()
    {
        var (export, _, _, studio) = Create("perene.mp3", status: HttpStatusCode.NotFound);

        Assert.False(await export.SaveAsync($"/audio/{Id}", "perene.mp3"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.NotNull(studio.Error);
    }

    private (AudioExport Export, FileHandler Handler, FakeShell Shell, StudioSession Studio) Create(
        string? chosenName, byte[]? body = null, int? failAfterBytes = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FileHandler(body ?? "ID3-audio"u8.ToArray(), failAfterBytes, status);
        var client = new TtsClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:41873/") });
        var shell = new FakeShell(chosenName is null ? null : Path.Combine(folder, chosenName));
        var studio = new StudioSession(client);
        return (new AudioExport(client, shell, studio), handler, shell, studio);
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private sealed class FileHandler(byte[] body, int? failAfterBytes, HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Stream content = failAfterBytes is { } limit ? new FailingStream(body.Length > limit ? body : new byte[limit * 2], limit) : new MemoryStream(body);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StreamContent(content) });
        }
    }

    private sealed class FailingStream(byte[] data, int failAfter) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => Position >= failAfter
            ? throw new IOException("connection reset") : base.Read(buffer, offset, Math.Min(count, failAfter - (int)Position));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Position >= failAfter
            ? throw new IOException("connection reset") : base.ReadAsync(buffer[..Math.Min(buffer.Length, failAfter - (int)Position)], cancellationToken);
    }

    private sealed class FakeShell(string? chosenPath) : IHostShell
    {
        public List<string> SuggestedNames { get; } = [];
        public List<FileFilter> Filters { get; } = [];
        public bool IsDesktop => true;
        public Task<string?> PickSaveFileAsync(string suggestedName, FileFilter filter)
        {
            SuggestedNames.Add(suggestedName);
            Filters.Add(filter);
            return Task.FromResult(chosenPath);
        }
        public Task<IReadOnlyList<string>> PickFilesAsync(FileFilter filter, bool multiple) => throw new NotSupportedException();
        public Task<string?> PickFolderAsync() => throw new NotSupportedException();
        public event Func<CloseRequest, Task>? CloseRequested { add { } remove { } }
        public void CompleteClose() { }
    }
}
