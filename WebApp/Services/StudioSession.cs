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
    public Task CreateVoice(Stream stream, string name, string language) =>
        Run(() => client.CreateVoiceAsync(stream, name, language, lifetime.Token));
    public Task Generate() => Run(() => client.SpeakAsync(SelectedVoice, SpeechText, lifetime.Token));
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
