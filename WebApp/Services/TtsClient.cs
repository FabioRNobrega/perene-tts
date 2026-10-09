using System.Net.Http.Json;
using System.Text.Json;
using PereneTts.Models;
namespace PereneTts.Services;

public sealed class TtsClient(HttpClient http)
{
    public async Task<WorkerHealth> HealthAsync(CancellationToken cancellation) =>
        await http.GetFromJsonAsync<WorkerHealth>("health", cancellation)
        ?? throw new HttpRequestException("Worker returned no health information.");
    public async Task<List<Voice>> VoicesAsync(CancellationToken cancellation) =>
        await http.GetFromJsonAsync<List<Voice>>("voices", cancellation) ?? [];
    public async Task<List<Creation>> CreationsAsync(CancellationToken cancellation) =>
        await http.GetFromJsonAsync<List<Creation>>("creations", cancellation) ?? [];
    public async Task<Job> CreateVoiceAsync(Stream audio, string extension, string name, string language, CancellationToken cancellation)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent(language), "language");
        // Only the validated extension is forwarded; the worker selects its decoder from it.
        form.Add(new StreamContent(audio), "audio", $"reference{extension}");
        using var response = await http.PostAsync("voices", form, cancellation);
        return await ReadAsync<Job>(response, cancellation);
    }
    public async Task<Job> SpeakAsync(string voiceId, string text, CancellationToken cancellation)
    {
        using var response = await http.PostAsJsonAsync("speech", new { voice_id = voiceId, text }, cancellation);
        return await ReadAsync<Job>(response, cancellation);
    }
    public async Task<Job> JobAsync(string id, CancellationToken cancellation)
    {
        using var response = await http.GetAsync($"jobs/{Guid.Parse(id):D}", cancellation);
        return await ReadAsync<Job>(response, cancellation);
    }
    public Task<HttpResponseMessage> OpenAudioAsync(Guid id, CancellationToken cancellation) =>
        http.GetAsync($"audio/{id:D}", HttpCompletionOption.ResponseHeadersRead, cancellation);

    public async Task<List<BatchSummary>> BatchesAsync(CancellationToken cancellation) =>
        await http.GetFromJsonAsync<List<BatchSummary>>("batches", cancellation) ?? [];
    public async Task<BatchDetail> BatchAsync(string id, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(BatchRoute(id), cancellation);
        return await ReadAsync<BatchDetail>(response, cancellation);
    }
    public async Task<BatchSummary> CreateBatchAsync(string name, string voiceId, IReadOnlyList<BatchUpload> files, CancellationToken cancellation)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent(voiceId), "voice_id");
        // Parts keep the submitted narration order; each file stream is opened only when HttpClient reads it.
        foreach (var file in files) form.Add(new StreamContent(file.Content), "files", file.Name);
        using var response = await http.PostAsync("batches", form, cancellation);
        return await ReadAsync<BatchSummary>(response, cancellation);
    }
    public Task<BatchSummary> PauseBatchAsync(string id, CancellationToken cancellation) => PostBatchAsync($"{BatchRoute(id)}/pause", cancellation);
    public Task<BatchSummary> ResumeBatchAsync(string id, CancellationToken cancellation) => PostBatchAsync($"{BatchRoute(id)}/resume", cancellation);
    public Task<BatchSummary> StopBatchAsync(string id, CancellationToken cancellation) => PostBatchAsync($"{BatchRoute(id)}/stop", cancellation);
    public Task<BatchSummary> RetryBatchAsync(string id, CancellationToken cancellation) => PostBatchAsync($"{BatchRoute(id)}/retry", cancellation);
    public Task<BatchSummary> RetryTrackAsync(string id, int number, CancellationToken cancellation) =>
        PostBatchAsync($"{BatchRoute(id)}/tracks/{number}/retry", cancellation);
    public async Task DeleteBatchAsync(string id, CancellationToken cancellation)
    {
        using var response = await http.DeleteAsync(BatchRoute(id), cancellation);
        await EnsureSuccessAsync(response, cancellation);
    }
    public Task<HttpResponseMessage> OpenBatchTrackAsync(Guid id, int number, CancellationToken cancellation) =>
        http.GetAsync($"batches/{id:D}/tracks/{number}/audio", HttpCompletionOption.ResponseHeadersRead, cancellation);
    public Task<HttpResponseMessage> OpenBatchArchiveAsync(Guid id, CancellationToken cancellation) =>
        http.GetAsync($"batches/{id:D}/archive", HttpCompletionOption.ResponseHeadersRead, cancellation);

    private static string BatchRoute(string id) => $"batches/{Guid.Parse(id):D}";
    private async Task<BatchSummary> PostBatchAsync(string route, CancellationToken cancellation)
    {
        using var response = await http.PostAsync(route, null, cancellation);
        return await ReadAsync<BatchSummary>(response, cancellation);
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        await EnsureSuccessAsync(response, cancellation);
        return await response.Content.ReadFromJsonAsync<T>(cancellation)
            ?? throw new HttpRequestException("Worker returned an empty response.");
    }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode) return;
        var message = $"TTS worker returned {(int)response.StatusCode}. Please retry.";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                message = detail.GetString() ?? message;
        }
        catch (JsonException) { }
        throw new InvalidOperationException(message);
    }
}
