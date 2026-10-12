using System.Net;
using System.Text;
using PereneTts.Models;
using PereneTts.Services;

namespace WebApp.Tests;

public sealed class LocalSourceFilesTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("perene-local-").FullName;

    private string Write(string relative, string text = "Text.")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void CollectsTextFilesRecursivelyAndCountsEverythingElseAsSkipped()
    {
        Write("book/InMiltonLumkyTerritoryIntro.txt");
        Write("book/chapter-001.txt");
        Write("book/part two/chapter-002.TXT");
        Write("book/cover.jpg");
        Write("book/.DS_Store");
        Write("book/._chapter-001.txt");

        var selection = LocalSourceFiles.Enumerate(Path.Combine(root, "book"));

        Assert.Equal(["InMiltonLumkyTerritoryIntro.txt", "chapter-001.txt", "chapter-002.TXT"],
            BatchRules.DefaultOrder(selection.Files.Select(file => file.Name)));
        Assert.Equal(3, selection.Skipped);
        Assert.False(selection.TooMany);
        Assert.All(selection.Files, file => Assert.DoesNotContain('/', file.Name));
    }

    [Fact]
    public void SymlinkedDirectoriesAreNotFollowed()
    {
        Write("outside/secret.txt");
        Write("book/chapter-001.txt");
        Directory.CreateSymbolicLink(Path.Combine(root, "book", "linked"), Path.Combine(root, "outside"));

        var selection = LocalSourceFiles.Enumerate(Path.Combine(root, "book"));

        Assert.Equal(["chapter-001.txt"], selection.Files.Select(file => file.Name));
    }

    [Fact]
    public void StopsOneFilePastTheBatchLimit()
    {
        for (var number = 1; number <= BatchRules.MaxFiles + 20; number++) Write($"many/chapter-{number:000}.txt");

        var selection = LocalSourceFiles.Enumerate(Path.Combine(root, "many"));

        Assert.Equal(BatchRules.MaxFiles + 1, selection.Files.Count);
        Assert.NotNull(BatchRules.SelectionError(selection.Files.Count, 0));
    }

    [Fact]
    public void LocalFileReportsNameAndMeasuredSize()
    {
        var file = new LocalSourceFile(Write("book/chapter-001.txt", "Hello"));

        Assert.Equal("chapter-001.txt", file.Name);
        Assert.Equal(5, file.Size);
    }

    [Fact]
    public async Task BoundedReadThrowsWhenTheFileGrewPastTheLimit()
    {
        var path = Write("book/growing.txt", "small");
        var file = new LocalSourceFile(path);
        await File.WriteAllTextAsync(path, new string('a', BatchRules.MaxFileBytes + 1));

        await using var stream = file.Open(BatchRules.MaxFileBytes, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task BoundedReadAllowsExactlyTheLimit()
    {
        var file = new LocalSourceFile(Write("book/exact.txt", new string('a', 1024)));

        await using var stream = file.Open(1024, CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal(1024, copy.Length);
    }

    [Fact]
    public async Task UploadSendsOnlyDisplayNamesNeverPaths()
    {
        Write("book/nested/deeper/chapter-001.txt", "One.");
        Write("book/chapter-002.txt", "Two.");
        var selection = LocalSourceFiles.Enumerate(Path.Combine(root, "book"));
        var handler = new MultipartHandler();
        var client = new TtsClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:41873/") });
        var uploads = BatchRules.DefaultOrder(selection.Files, file => file.Name)
            .Select(file => new BatchUpload(file.Name, file.Open(BatchRules.MaxFileBytes, CancellationToken.None))).ToList();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateBatchAsync("Book", Guid.NewGuid().ToString(), uploads, CancellationToken.None));

        Assert.Equal(["chapter-001.txt", "chapter-002.txt"], handler.FileNames);
        Assert.DoesNotContain(root, handler.Body);
        Assert.Contains("One.", handler.Body);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class MultipartHandler : HttpMessageHandler
    {
        public List<string> FileNames { get; } = [];
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var part in (MultipartFormDataContent)request.Content!)
                if (part.Headers.ContentDisposition is { } disposition && (disposition.FileNameStar ?? disposition.FileName) is { } name)
                    FileNames.Add(name.Trim('"'));
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            // Empty body: the client reports an empty response, which ends the test after the request was recorded.
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("null", Encoding.UTF8, "application/json") };
        }
    }
}
