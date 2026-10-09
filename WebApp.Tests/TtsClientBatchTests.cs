using System.Net;
using System.Text;
using PereneTts.Models;
using PereneTts.Services;

namespace WebApp.Tests;

public sealed class TtsClientBatchTests
{
    private const string SummaryJson = """
        {"batch_id":"6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f","name":"Lumky","voice_id":"0b8f2f5e-2a2a-4b4b-8c8c-1d1d1d1d1d1d",
         "voice_name":"Reader","language_id":"pt","state":"running","message":"Converting",
         "created_at":"2026-10-08T10:00:00+00:00","updated_at":"2026-10-08T10:05:00+00:00","track_count":18,
         "tracks_completed":4,"tracks_failed":0,"chunk_count":1500,"chunks_done":320,"chunk_seconds_total":640.5,
         "chunks_done_total":320,"current_track_number":5,"current_track_name":"chapter-004.txt",
         "current_chunks_done":12,"current_chunk_count":90}
        """;

    private static (TtsClient Client, RecordingHandler Handler) Create(HttpStatusCode status = HttpStatusCode.OK, string body = SummaryJson)
    {
        var handler = new RecordingHandler(status, body);
        return (new TtsClient(new HttpClient(handler) { BaseAddress = new Uri("http://tts:5081/") }), handler);
    }

    [Fact]
    public async Task CreateBatchSendsNameVoiceAndFilesInSubmittedOrder()
    {
        var (client, handler) = Create(HttpStatusCode.Accepted);
        var voiceId = Guid.NewGuid().ToString();
        BatchUpload[] files =
        [
            new("InMiltonLumkyTerritoryIntro.txt", new MemoryStream("Intro."u8.ToArray())),
            new("chapter-001.txt", new MemoryStream("One."u8.ToArray())),
            new("InMiltonLumkyTerritoryOutro.txt", new MemoryStream("Outro."u8.ToArray()))
        ];

        var summary = await client.CreateBatchAsync("Lumky", voiceId, files, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://tts:5081/batches", handler.Uri?.ToString());
        Assert.Equal(["name", "voice_id", "files", "files", "files"], handler.PartNames);
        Assert.Equal(["InMiltonLumkyTerritoryIntro.txt", "chapter-001.txt", "InMiltonLumkyTerritoryOutro.txt"], handler.FileNames);
        Assert.Contains("Lumky", handler.Body);
        Assert.Contains(voiceId, handler.Body);
        Assert.True(handler.Body.IndexOf("Intro.", StringComparison.Ordinal) < handler.Body.IndexOf("One.", StringComparison.Ordinal));
        Assert.Equal("running", summary.State);
    }

    [Fact]
    public async Task ActionsUseCanonicalRoutes()
    {
        var id = Guid.NewGuid();
        var upper = id.ToString("D").ToUpperInvariant();
        var (client, handler) = Create();

        await client.PauseBatchAsync(upper, CancellationToken.None);
        Assert.Equal($"/batches/{id:D}/pause", handler.Uri?.AbsolutePath);
        await client.ResumeBatchAsync(id.ToString("N"), CancellationToken.None);
        Assert.Equal($"/batches/{id:D}/resume", handler.Uri?.AbsolutePath);
        await client.StopBatchAsync(upper, CancellationToken.None);
        Assert.Equal($"/batches/{id:D}/stop", handler.Uri?.AbsolutePath);
        await client.RetryBatchAsync(upper, CancellationToken.None);
        Assert.Equal($"/batches/{id:D}/retry", handler.Uri?.AbsolutePath);
        await client.RetryTrackAsync(upper, 7, CancellationToken.None);
        Assert.Equal($"/batches/{id:D}/tracks/7/retry", handler.Uri?.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Method);

        var (deleting, deleteHandler) = Create(HttpStatusCode.NoContent, "");
        await deleting.DeleteBatchAsync(upper, CancellationToken.None);
        Assert.Equal(HttpMethod.Delete, deleteHandler.Method);
        Assert.Equal($"/batches/{id:D}", deleteHandler.Uri?.AbsolutePath);

        using var track = await client.OpenBatchTrackAsync(id, 3, CancellationToken.None);
        Assert.Equal($"/batches/{id:D}/tracks/3/audio", handler.Uri?.AbsolutePath);
        using var archive = await client.OpenBatchArchiveAsync(id, CancellationToken.None);
        Assert.Equal($"/batches/{id:D}/archive", handler.Uri?.AbsolutePath);
        await Assert.ThrowsAsync<FormatException>(() => client.PauseBatchAsync("../voices", CancellationToken.None));
    }

    [Fact]
    public async Task ConflictDetailBecomesInvalidOperationMessage()
    {
        var (client, _) = Create(HttpStatusCode.Conflict, """{"detail":"Only paused batches can be resumed"}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ResumeBatchAsync(Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.Equal("Only paused batches can be resumed", error.Message);
    }

    [Fact]
    public async Task ErrorsWithoutDetailUseTheStatusCode()
    {
        var (client, _) = Create(HttpStatusCode.ServiceUnavailable, "<html>");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteBatchAsync(Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.Equal("TTS worker returned 503. Please retry.", error.Message);
    }

    [Fact]
    public async Task BatchesDeserializeSnakeCaseSummaries()
    {
        var (client, handler) = Create(body: $"[{SummaryJson}]");

        var batch = Assert.Single(await client.BatchesAsync(CancellationToken.None));

        Assert.Equal("/batches", handler.Uri?.AbsolutePath);
        Assert.Equal("Lumky", batch.Name);
        Assert.Equal("pt", batch.LanguageId);
        Assert.Equal(4, batch.TracksCompleted);
        Assert.Equal(320, batch.ChunksDone);
        Assert.Equal(640.5, batch.ChunkSecondsTotal);
        Assert.Equal(5, batch.CurrentTrackNumber);
        Assert.Equal("chapter-004.txt", batch.CurrentTrackName);
    }

    [Fact]
    public async Task BatchDetailDeserializesTracks()
    {
        var detail = $$"""{"batch":{{SummaryJson}},"tracks":[{"number":1,"display_name":"Intro.txt","output_name":"Lumky 001.mp3","state":"completed","chunk_count":3,"chunks_done":3,"error":null,"duration_seconds":12.5,"completed_at":"2026-10-08T10:01:00+00:00"}]}""";
        var (client, _) = Create(body: detail);

        var result = await client.BatchAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        var track = Assert.Single(result.Tracks);
        Assert.Equal("Lumky 001.mp3", track.OutputName);
        Assert.Equal(12.5, track.DurationSeconds);
        Assert.Equal("running", result.Batch.State);
    }

    [Fact]
    public async Task CreationWithoutBatchKeysStillDeserializes()
    {
        const string legacy = """[{"job_id":"j","kind":"speech","voice_id":null,"voice_name":"Reader","language_id":"en","text":"Hi","created_at":"2026-10-08T10:00:00+00:00"}]""";
        var (client, _) = Create(body: legacy);

        var creation = Assert.Single(await client.CreationsAsync(CancellationToken.None));

        Assert.Null(creation.BatchId);
        Assert.Null(creation.TrackNumber);
        Assert.False(BatchRules.IsBatchTrack(creation));
    }

    [Fact]
    public async Task BatchCreationKeysDeserialize()
    {
        var batchId = Guid.NewGuid();
        var json = $$"""[{"job_id":"{{batchId}}-003","kind":"batch","voice_id":null,"voice_name":"Reader","language_id":"en","text":"","created_at":"2026-10-08T10:00:00+00:00","batch_id":"{{batchId}}","batch_name":"Lumky","track_number":3,"track_count":18,"source_name":"chapter-002.txt"}]""";
        var (client, _) = Create(body: json);

        var creation = Assert.Single(await client.CreationsAsync(CancellationToken.None));

        Assert.Equal($"/batches/{batchId}/tracks/3", BatchRules.AudioUrl(creation));
        Assert.Equal("chapter-002.txt", creation.SourceName);
    }

    [Fact]
    public async Task HealthDefaultsBatchEnabledForOlderWorkers()
    {
        var (client, _) = Create(body: """{"model_ready":true,"device":"cpu","load_error":null}""");
        Assert.True((await client.HealthAsync(CancellationToken.None)).BatchEnabled);
        var (disabled, _) = Create(body: """{"model_ready":true,"device":"cpu","load_error":null,"batch_enabled":false}""");
        Assert.False((await disabled.HealthAsync(CancellationToken.None)).BatchEnabled);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string Body { get; private set; } = "";
        public List<string> PartNames { get; } = [];
        public List<string> FileNames { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            PartNames.Clear();
            FileNames.Clear();
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    var disposition = part.Headers.ContentDisposition!;
                    PartNames.Add(disposition.Name!.Trim('"'));
                    if ((disposition.FileNameStar ?? disposition.FileName) is { } fileName) FileNames.Add(fileName.Trim('"'));
                }
            }
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
