using System.Text.RegularExpressions;
namespace PereneTts.Services;

/// <summary>Desktop downloads: asks the shell where to save, then streams the worker file to "&lt;path&gt;.part" and renames it.</summary>
public sealed partial class AudioExport(TtsClient client, IHostShell shell, StudioSession studio)
{
    /// <summary>Saves the file behind one of the studio's three download routes; returns whether a file was written.</summary>
    public async Task<bool> SaveAsync(string href, string suggestedName, CancellationToken cancellation = default)
    {
        var open = Resolve(href);
        if (open is null)
        {
            studio.SetError("This file cannot be saved.");
            return false;
        }
        var filter = suggestedName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? FileFilter.Zip : FileFilter.Mp3;
        var path = await shell.PickSaveFileAsync(suggestedName, filter);
        if (string.IsNullOrEmpty(path)) return false;
        var part = path + ".part";
        var name = Path.GetFileName(path);
        try
        {
            using (var response = await open(cancellation))
            {
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Worker returned {(int)response.StatusCode}.");
                await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await response.Content.CopyToAsync(target, cancellation);
            }
            File.Move(part, path, overwrite: true);
            studio.SetNotice($"Saved {name}");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        {
            TryDelete(part);
            if (!cancellation.IsCancellationRequested)
                studio.SetError($"Could not save {name}. Check that the folder is writable and try again.");
            return false;
        }
    }

    /// <summary>Only the studio's own download routes map to a worker request; anything else is rejected.</summary>
    public Func<CancellationToken, Task<HttpResponseMessage>>? Resolve(string href)
    {
        var route = href.Split('?', 2)[0];
        if (AudioRoute().Match(route) is { Success: true } audio)
        {
            var id = Guid.Parse(audio.Groups["id"].Value);
            return token => client.OpenAudioAsync(id, token);
        }
        if (TrackRoute().Match(route) is { Success: true } track && int.TryParse(track.Groups["number"].Value, out var number))
        {
            var id = Guid.Parse(track.Groups["id"].Value);
            return token => client.OpenBatchTrackAsync(id, number, token);
        }
        if (ArchiveRoute().Match(route) is { Success: true } archive)
        {
            var id = Guid.Parse(archive.Groups["id"].Value);
            return token => client.OpenBatchArchiveAsync(id, token);
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private const string Guid36 = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    [GeneratedRegex($"^/audio/(?<id>{Guid36})$")] private static partial Regex AudioRoute();
    [GeneratedRegex($"^/batches/(?<id>{Guid36})/tracks/(?<number>[0-9]{{1,4}})$")] private static partial Regex TrackRoute();
    [GeneratedRegex($"^/batches/(?<id>{Guid36})/archive$")] private static partial Regex ArchiveRoute();
}
