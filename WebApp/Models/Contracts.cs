using System.Text.Json.Serialization;
namespace PereneTts.Models;

public sealed record Voice(
    [property: JsonPropertyName("voice_id")] string VoiceId,
    [property: JsonPropertyName("language_id")] string LanguageId,
    [property: JsonPropertyName("name")] string Name);
public sealed record WorkerHealth(
    [property: JsonPropertyName("model_ready")] bool ModelReady,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("load_error")] string? LoadError);
public sealed record Job(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("percent")] int Percent,
    [property: JsonPropertyName("voice_id")] string? VoiceId,
    [property: JsonPropertyName("error")] string? Error);

public sealed record Creation(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("voice_id")] string? VoiceId,
    [property: JsonPropertyName("voice_name")] string VoiceName,
    [property: JsonPropertyName("language_id")] string? LanguageId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
