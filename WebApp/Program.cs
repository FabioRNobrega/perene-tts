using PereneTts.Components;
using PereneTts.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient<TtsClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Tts:BaseUrl"] ?? "http://tts:5081/");
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
app.MapGet("/audio/{id:guid}", async (Guid id, TtsClient client, HttpContext context) =>
{
    try
    {
        using var response = await client.OpenAudioAsync(id, context.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            return;
        }
        context.Response.ContentType = "audio/mpeg";
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Query.ContainsKey("download"))
            context.Response.Headers.ContentDisposition = $"attachment; filename=perene-{id}.mp3";
        context.Response.ContentLength = response.Content.Headers.ContentLength;
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (HttpRequestException) { context.Response.StatusCode = 503; }
    catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    { context.Response.StatusCode = 504; }
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
