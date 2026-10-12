using System.Diagnostics;
using PereneTts.Services;
namespace PereneTts.Desktop;

public sealed class WorkerStartException(string message) : Exception(message);

/// <summary>The bundled FastAPI worker as a loopback sidecar: launched with its settings in the environment (never argv),
/// ready once it prints its port, logged to worker.log, and stopped by closing its stdin.</summary>
public sealed class WorkerProcess(DesktopPaths paths, string token, string device) : IWorkerStatus, IAsyncDisposable
{
    private readonly Lock logLock = new();
    private Process? process;
    private StreamWriter? log;
    private Task pumps = Task.CompletedTask;
    private volatile bool exited;

    public Uri? BaseAddress { get; private set; }
    public bool HasExited => exited;

    /// <summary>Settings the worker receives; the token travels only here, so `ps` never shows it.</summary>
    public static IReadOnlyDictionary<string, string> Settings(DesktopPaths paths, string token, string device) =>
        new Dictionary<string, string>
        {
            ["TTS_API_TOKEN"] = token,
            ["TTS_DATA_DIR"] = paths.Studio,
            ["TTS_MODEL_DIR"] = paths.Model,
            ["TTS_FFMPEG"] = paths.Ffmpeg,
            ["TTS_DEVICE"] = device,
            ["TTS_BATCH_ENABLED"] = "true",
            ["PYTORCH_ENABLE_MPS_FALLBACK"] = "1",
            ["HF_HUB_OFFLINE"] = "1",
            ["TRANSFORMERS_OFFLINE"] = "1",
            ["HF_HOME"] = Path.Combine(paths.Cache, "huggingface"),
            ["NUMBA_CACHE_DIR"] = Path.Combine(paths.Cache, "numba"),
        };

    /// <summary>Isolated mode (-I) ignores PYTHON* variables, user site-packages and the working directory, so only the
    /// bundled packages load. Compiled bytecode goes to the cache, never into the signed bundle.</summary>
    public static IReadOnlyList<string> Arguments(DesktopPaths paths) =>
        ["-I", "-X", $"pycache_prefix={Path.Combine(paths.Cache, "pycache")}", "-m", "app.desktop"];

    public async Task StartAsync(TimeSpan readyTimeout)
    {
        OpenLog();
        var start = new ProcessStartInfo(paths.Python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = paths.Cache,
        };
        foreach (var argument in Arguments(paths)) start.ArgumentList.Add(argument);
        foreach (var (name, value) in Settings(paths, token, device)) start.Environment[name] = value;
        try
        {
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnExited();
            process.Start();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process?.Dispose();
            process = null;
            Write($"Worker could not start: {error.Message}");
            throw new WorkerStartException("The voice engine could not start.");
        }
        var errors = Pump(process.StandardError);
        using var timeout = new CancellationTokenSource(readyTimeout);
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                    ?? throw new WorkerStartException("The voice engine stopped while starting.");
                if (WorkerReadyLine.TryParse(line, out var port))
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}/");
                    Write($"Worker ready on 127.0.0.1:{port}");
                    break;
                }
                Write(line);
            }
        }
        catch (OperationCanceledException)
        {
            throw new WorkerStartException($"The voice engine did not start within {readyTimeout.TotalSeconds:0} seconds.");
        }
        pumps = Task.WhenAll(errors, Pump(process.StandardOutput));
    }

    /// <summary>Graceful first (the worker's lifespan stops the batch runner), then the whole process tree.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(grace);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    Write($"Worker did not stop within {grace.TotalSeconds:0} seconds; killing it");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Write($"Worker stop failed: {error.Message}");
        }
        await pumps.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(10));
        Release();
    }

    public void Release()
    {
        process?.Dispose();
        lock (logLock)
        {
            log?.Dispose();
            log = null;
        }
    }

    private void OnExited()
    {
        exited = true;
        int? code = null;
        try { code = process?.ExitCode; }
        catch (InvalidOperationException) { }
        Write($"Worker exited with code {code?.ToString() ?? "unknown"}");
    }

    private async Task Pump(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) Write(line);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private void OpenLog()
    {
        var path = paths.WorkerLog;
        if (File.Exists(path)) File.Move(path, Path.Combine(paths.Logs, "worker.previous.log"), overwrite: true);
        log = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    private void Write(string line)
    {
        lock (logLock) log?.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {line}");
    }
}
