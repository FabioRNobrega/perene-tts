using PereneTts.Components;
using PereneTts.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient<TtsClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Tts:BaseUrl"] ?? "http://tts:5081/");
    // With ResponseHeadersRead this bounds only the wait for headers, so long downloads keep streaming.
    client.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddScoped<StudioSession>();
var app = builder.Build();
app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/health", () => Results.Ok(new { status = "alive" }));
app.MapGet("/error", () => Results.Problem("The application encountered an error. Please reload and try again."));
app.MapGet("/audio/{id:guid}", (Guid id, TtsClient client, HttpContext context) =>
    StreamFromWorker(context, token => client.OpenAudioAsync(id, token), "audio/mpeg", $"attachment; filename=perene-{id}.mp3"));
app.MapGet("/batches/{id:guid}/tracks/{number:int}", (Guid id, int number, TtsClient client, HttpContext context) =>
    StreamFromWorker(context, token => client.OpenBatchTrackAsync(id, number, token), "audio/mpeg"));
app.MapGet("/batches/{id:guid}/archive", (Guid id, TtsClient client, HttpContext context) =>
    StreamFromWorker(context, token => client.OpenBatchArchiveAsync(id, token), "application/zip", alwaysDownload: true));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

// Streams a worker file without buffering; downloads use the given name or the worker's sanitized Content-Disposition.
static async Task StreamFromWorker(HttpContext context, Func<CancellationToken, Task<HttpResponseMessage>> open,
    string contentType, string? disposition = null, bool alwaysDownload = false)
{
    try
    {
        using var response = await open(context.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            return;
        }
        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = "no-store";
        if (alwaysDownload || context.Request.Query.ContainsKey("download"))
        {
            disposition ??= response.Content.Headers.TryGetValues("Content-Disposition", out var values) ? string.Join(", ", values) : null;
            if (disposition is not null) context.Response.Headers.ContentDisposition = disposition;
        }
        context.Response.ContentLength = response.Content.Headers.ContentLength;
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (HttpRequestException) { context.Response.StatusCode = 503; }
    catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    { context.Response.StatusCode = 504; }
}
