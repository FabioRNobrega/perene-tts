using PereneTts.Models;
namespace PereneTts.Services;

// One studio session per Blazor circuit keeps generation visible while navigating.
public sealed class StudioSession(TtsClient client) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? polling;
    public event Action? Changed;
    public WorkerHealth? Health { get; private set; }
    public List<Voice> Voices { get; private set; } = [];
    public List<Creation> Creations { get; private set; } = [];
    // Batch conversion runs in the worker; the circuit only caches summaries for display.
    public List<BatchSummary> Batches { get; private set; } = [];
    public bool BatchesLoaded { get; private set; }
    public BatchSummary? ActiveBatch => BatchRules.ActiveBatch(Batches);
    public bool BatchEnabled => Health?.BatchEnabled ?? true;
    public Job? Job { get; private set; }
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    public string SelectedVoice { get; set; } = "";
    public string SpeechText { get; set; } = "";
    private const string Unavailable = "The voice engine is unavailable. Check that the TTS worker is running.";
    public string HealthText => Health is null ? "Connecting to voice engine…" : Health.LoadError ??
        (Health.ModelReady ? $"Voice engine ready · {Health.Device.ToUpperInvariant()}" : "Loading voice engine…");
    public static string LanguageName(string? language) => language switch
    { "en" => "English", "pt" => "Português", "sv" => "Svenska", _ => "Unknown language" };
    public static string PreviewText(string language) => language switch
    {
        "pt" => "Olá! Bem-vindo ao PereneTTS. Este é um teste rápido de voz para ouvir como minha voz soa natural e clara.",
        "sv" => "Hej! Välkommen till PereneTTS. Det här är ett snabbt rösttest för att höra hur naturlig och tydlig min röst låter.",
        _ => "Hello! Welcome to PereneTTS. This is a quick voice test to hear how natural and clear my voice sounds."
    };
    public void Start() => polling ??= Poll();
    public void SetError(string? error) { Error = error; Changed?.Invoke(); }
    public async Task Refresh()
    {
        try
        {
            Health = await client.HealthAsync(lifetime.Token);
            Voices = await client.VoicesAsync(lifetime.Token);
            Creations = await client.CreationsAsync(lifetime.Token);
            Batches = await client.BatchesAsync(lifetime.Token);
            BatchesLoaded = true;
            if (Error == Unavailable) Error = null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { Health = null; Error = Unavailable; }
        Changed?.Invoke();
    }
    private async Task Poll()
    {
        try
        {
            await Refresh();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(lifetime.Token)) await Refresh();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public Task CreateVoice(Stream stream, string extension, string name, string language) =>
        Run(() => client.CreateVoiceAsync(stream, extension, name, language, lifetime.Token));
    public Task Generate() => Run(() => client.SpeakAsync(SelectedVoice, SpeechText, lifetime.Token));
    public async Task<BatchSummary?> CreateBatch(string name, string voiceId, IReadOnlyList<BatchUpload> files)
    {
        BatchSummary? created = null;
        return await BatchAction(async () => created = await client.CreateBatchAsync(name, voiceId, files, lifetime.Token)) ? created : null;
    }
    public Task<bool> PauseBatch(string id) => BatchAction(() => client.PauseBatchAsync(id, lifetime.Token));
    public Task<bool> ResumeBatch(string id) => BatchAction(() => client.ResumeBatchAsync(id, lifetime.Token));
    public Task<bool> StopBatch(string id) => BatchAction(() => client.StopBatchAsync(id, lifetime.Token));
    public Task<bool> RetryBatch(string id) => BatchAction(() => client.RetryBatchAsync(id, lifetime.Token));
    public Task<bool> RetryTrack(string id, int number) => BatchAction(() => client.RetryTrackAsync(id, number, lifetime.Token));
    public Task<bool> DeleteBatch(string id) => BatchAction(() => client.DeleteBatchAsync(id, lifetime.Token));
    /// <summary>Track details for an expanded row; null when the batch is gone or the worker is unreachable.</summary>
    public async Task<BatchDetail?> LoadBatch(string id)
    {
        try { return await client.BatchAsync(id, lifetime.Token); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or FormatException) { return null; }
    }
    private async Task<bool> BatchAction(Func<Task> action)
    {
        try
        {
            Error = null;
            await action();
            await Refresh();
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            Error = ex is InvalidOperationException ? ex.Message : "The connection was interrupted. Refresh and try again.";
            Changed?.Invoke();
            return false;
        }
    }
    private async Task Run(Func<Task<Job>> start)
    {
        if (Busy) return;
        Busy = true; Error = null; Job = null; Changed?.Invoke();
        try
        {
            Job = await start(); Changed?.Invoke();
            while (Job.Status == "running")
            {
                await Task.Delay(1000, lifetime.Token);
                Job = await client.JobAsync(Job.JobId, lifetime.Token);
                Changed?.Invoke();
            }
            if (Job.Status == "failed") Error = Job.Error ?? "Audio generation failed. Please try again.";
            if (Job.Status == "completed" && Job.VoiceId is not null) SelectedVoice = Job.VoiceId;
            await Refresh();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        { Error = ex is InvalidOperationException ? ex.Message : "The connection was interrupted. Refresh and try again."; }
        finally { Busy = false; Changed?.Invoke(); }
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (polling is not null) await polling;
        lifetime.Dispose();
    }
}
