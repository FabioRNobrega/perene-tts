using System.Text.Json.Serialization;
namespace PereneTts.Models;

public sealed record Voice(
    [property: JsonPropertyName("voice_id")] string VoiceId,
    [property: JsonPropertyName("language_id")] string LanguageId,
    [property: JsonPropertyName("name")] string Name);
public sealed record WorkerHealth(
    [property: JsonPropertyName("model_ready")] bool ModelReady,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("load_error")] string? LoadError,
    [property: JsonPropertyName("batch_enabled")] bool BatchEnabled = true);
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
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("batch_id")] string? BatchId = null,
    [property: JsonPropertyName("batch_name")] string? BatchName = null,
    [property: JsonPropertyName("track_number")] int? TrackNumber = null,
    [property: JsonPropertyName("track_count")] int? TrackCount = null,
    [property: JsonPropertyName("source_name")] string? SourceName = null);

// List rows carry no track arrays or text; details add the tracks.
public sealed record BatchSummary(
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("voice_id")] string VoiceId,
    [property: JsonPropertyName("voice_name")] string VoiceName,
    [property: JsonPropertyName("language_id")] string LanguageId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("track_count")] int TrackCount,
    [property: JsonPropertyName("tracks_completed")] int TracksCompleted,
    [property: JsonPropertyName("tracks_failed")] int TracksFailed,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("chunks_done")] int ChunksDone,
    [property: JsonPropertyName("chunk_seconds_total")] double ChunkSecondsTotal,
    [property: JsonPropertyName("chunks_done_total")] int ChunksDoneTotal,
    [property: JsonPropertyName("current_track_number")] int? CurrentTrackNumber,
    [property: JsonPropertyName("current_track_name")] string? CurrentTrackName,
    [property: JsonPropertyName("current_chunks_done")] int? CurrentChunksDone,
    [property: JsonPropertyName("current_chunk_count")] int? CurrentChunkCount);

public sealed record BatchTrack(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("output_name")] string OutputName,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("chunks_done")] int ChunksDone,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt);

public sealed record BatchDetail(
    [property: JsonPropertyName("batch")] BatchSummary Batch,
    [property: JsonPropertyName("tracks")] List<BatchTrack> Tracks);

// One source file to forward; Name is a display label only and never a storage path.
public sealed record BatchUpload(string Name, Stream Content);
