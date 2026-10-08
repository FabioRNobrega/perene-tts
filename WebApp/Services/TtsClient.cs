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
    public async Task<Job> CreateVoiceAsync(Stream audio, string name, string language, CancellationToken cancellation)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent(language), "language");
        form.Add(new StreamContent(audio), "audio", "reference.mp3");
        using var response = await http.PostAsync("voices", form, cancellation);
        return await ReadJobAsync(response, cancellation);
    }
    public async Task<Job> SpeakAsync(string voiceId, string text, CancellationToken cancellation)
    {
        using var response = await http.PostAsJsonAsync("speech", new { voice_id = voiceId, text }, cancellation);
        return await ReadJobAsync(response, cancellation);
    }
    public async Task<Job> JobAsync(string id, CancellationToken cancellation)
    {
        using var response = await http.GetAsync($"jobs/{Guid.Parse(id):D}", cancellation);
        return await ReadJobAsync(response, cancellation);
    }
    public Task<HttpResponseMessage> OpenAudioAsync(Guid id, CancellationToken cancellation) =>
        http.GetAsync($"audio/{id:D}", HttpCompletionOption.ResponseHeadersRead, cancellation);
    private static async Task<Job> ReadJobAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode)
        {
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
        return await response.Content.ReadFromJsonAsync<Job>(cancellation)
            ?? throw new HttpRequestException("Worker returned an empty response.");
    }
}
