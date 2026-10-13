using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Photino.NET;
using PereneTts.Models;
using PereneTts.Services;
namespace PereneTts.Desktop;

/// <summary>Desktop mode: single instance, bundled worker sidecar, loopback-only studio, and the Perene TTS window.
/// Runs synchronously on the process main thread, which AppKit requires for the window.</summary>
public static class DesktopHost
{
    private const string Title = DesktopPaths.AppName;
    private const string CannotStart = "Perene TTS cannot start";
    // ⚠️ Pending the Phase 1b CPU/MPS measurement (Open Questions); TTS_DEVICE in the environment overrides it.
    private const string DefaultDevice = "auto";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan WorkerGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WebGrace = TimeSpan.FromSeconds(5);

    public static int Run()
    {
        DesktopPaths paths;
        try
        {
            paths = DesktopPaths.Resolve(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AppContext.BaseDirectory, Environment.GetEnvironmentVariables());
            paths.EnsureDirectories();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            NativeAlert.Show(CannotStart, error.Message, critical: true);
            return 1;
        }

        using var instance = InstanceLock.TryAcquire(paths.InstanceLock);
        if (instance is null)
        {
            NativeAlert.Show(Title, "Perene TTS is already running.", critical: false);
            return 0;
        }

        var workerToken = DesktopAccess.NewSecret();
        var device = Environment.GetEnvironmentVariable("TTS_DEVICE") is { Length: > 0 } requested ? requested : DefaultDevice;
        var worker = new WorkerProcess(paths, workerToken, device);
        try { worker.StartAsync(ReadyTimeout).GetAwaiter().GetResult(); }
        catch (WorkerStartException error)
        {
            worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            NativeAlert.Show(CannotStart, $"{error.Message} Details are in {paths.WorkerLog}.", critical: true);
            return 1;
        }

        WebApplication? app = null;
        var shutdownStarted = 0;
        void Shutdown()
        {
            if (Interlocked.Exchange(ref shutdownStarted, 1) == 1) return;
            try
            {
                using var timeout = new CancellationTokenSource(WebGrace);
                app?.StopAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
            // Closing stdin lets the worker's lifespan stop the batch runner; recover() finishes a stopping batch next launch.
            worker.StopAsync(WorkerGrace).GetAwaiter().GetResult();
            worker.Release();
            instance.Dispose();
        }

        try
        {
            var window = new PhotinoWindow();
            CloseCoordinator? coordinator = null;
            coordinator = new CloseCoordinator(() => AskFromHost(app, window, coordinator!), window.Close, Shutdown);
            var shell = new PhotinoHostShell(window, coordinator, paths.Music);
            var access = DesktopAccess.Create();
            app = BuildApp(paths, worker, workerToken, shell, access);
            app.StartAsync().GetAwaiter().GetResult();
            var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>()
                .Addresses.First().TrimEnd('/');
            window.SetTitle(Title)
                .SetUseOsDefaultSize(false)
                .SetSize(1280, 800)
                .SetMinSize(720, 520)
                .Center()
                .RegisterWindowClosingHandler(coordinator.OnWindowClosing)
                .Load(new Uri($"{address}/?launch={Uri.EscapeDataString(access.LaunchToken)}"));
            if (!MacAppKit.InstallCloseHooks(coordinator.WindowShouldClose, coordinator.ShouldTerminate))
                app.Logger.LogWarning("Close confirmation is unavailable; closing the window quits immediately");
            window.WaitForClose();
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            app?.Logger.LogError(error, "Desktop host failed");
            NativeAlert.Show(Title, "Perene TTS stopped because of an unexpected error. Close and reopen Perene TTS.", critical: true);
            return 1;
        }
        finally { Shutdown(); }
    }

    private static WebApplication BuildApp(DesktopPaths paths, WorkerProcess worker, string workerToken, IHostShell shell, DesktopAccess access)
    {
        // Content root is Contents/Resources/dotnet, where the publish output keeps wwwroot and the static web assets manifest.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        // Host filtering rejects other Host headers (DNS rebinding); Learn: Kestrel host filtering.
        builder.Configuration["AllowedHosts"] = "127.0.0.1";
        // ListenLocalhost does not support port 0 (Learn: Kestrel endpoints), so bind the IPv4 loopback explicitly.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        // Keys leave ~/.aspnet; a fixed name keeps them valid when App Translocation moves the bundle.
        builder.Services.AddDataProtection().SetApplicationName("perene-tts").PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));
        builder.Services.AddSingleton(shell);
        builder.Services.AddSingleton<IWorkerStatus>(worker);
        builder.Services.AddSingleton(access);
        StudioApp.AddStudio(builder, new StudioHostOptions(worker.BaseAddress!, workerToken));
        var app = builder.Build();
        app.UseMiddleware<DesktopAccessMiddleware>();
        StudioApp.MapStudio(app);
        return app;
    }

    /// <summary>No studio page answered the close (not loaded or crashed): ask with a native message instead.</summary>
    private static async Task AskFromHost(WebApplication? app, PhotinoWindow window, CloseCoordinator coordinator)
    {
        if (app?.Services.GetRequiredService<TtsClient>() is not { } client)
        {
            coordinator.CompleteClose();
            return;
        }
        CloseDecision decision;
        try { decision = DesktopRules.Decide(false, await client.BatchesAsync(CancellationToken.None)); }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The worker is unreachable, so nothing is converting that could be stopped cleanly.
            coordinator.CompleteClose();
            return;
        }
        if (decision.Kind == CloseKind.CloseNow)
        {
            coordinator.CompleteClose();
            return;
        }
        var answer = window.ShowMessage(DesktopRules.Title(decision), DesktopRules.Message(decision),
            PhotinoDialogButtons.OkCancel, PhotinoDialogIcon.Warning);
        if (answer != PhotinoDialogResult.Ok) return;
        try
        {
            foreach (var batch in decision.Batches) await client.StopBatchAsync(batch.BatchId, CancellationToken.None);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            window.ShowMessage(Title, "The conversion could not be stopped. Try closing again.", PhotinoDialogButtons.Ok, PhotinoDialogIcon.Error);
            return;
        }
        coordinator.CompleteClose();
    }
}
