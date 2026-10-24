using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RalFsrHelper.Core;

/// <summary>
/// Downloads the FSR 4.1.2 driver files from ROG Ally Life's file host, so users never handle them by hand.
/// A file is only used after it passes the same checks as a manual import: a valid AMD or WHQL signature on an AMD
/// driver file, at least version 2.4.0.4370. Once a day the host is asked (with ETags) whether a file changed;
/// uploading a newer DLL to the same URL updates every user.
/// </summary>
public static class Fsr4Download
{
    public static readonly IReadOnlyList<(string FileName, string Url)> Sources = new[]
    {
        (InstallConfig.Dx12Fsr4File, "https://media.rogallylife.com/fsr4/amdxcffx64.dll"),
        (InstallConfig.VulkanFsr4File, "https://media.rogallylife.com/fsr4/amdvkffx64.dll"),
    };

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static string StatePath => Path.Combine(AppPaths.Fsr4Dir, "download-state.json");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) }; // ~80 MB per file on handheld Wi-Fi
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RALFsr4Lite", "0.1"));
        return c;
    }

    private sealed class State
    {
        public DateTime LastCheck { get; set; }
        public Dictionary<string, string> ETags { get; set; } = new();
    }

    /// <summary>Result of a run: whether any file changed, plus a message when something went wrong.</summary>
    public sealed record Result(bool Changed, string? Problem);

    /// <summary>
    /// Downloads missing files, or checks for updated ones if a day has passed (or <paramref name="force"/>).
    /// </summary>
    public static async Task<Result> EnsureAsync(IProgress<string> progress, bool force = false, CancellationToken ct = default)
    {
        AppPaths.Ensure();
        var state = LoadState();
        var missing = Sources.Any(s => !File.Exists(Path.Combine(AppPaths.Fsr4Dir, s.FileName)));
        if (!missing && !force && DateTime.UtcNow - state.LastCheck < CheckInterval) return new Result(false, null);

        var changed = false;
        string? problem = null;

        foreach (var (fileName, url) in Sources)
        {
            var target = Path.Combine(AppPaths.Fsr4Dir, fileName);
            var temp = target + ".download";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (File.Exists(target) && state.ETags.TryGetValue(fileName, out var etag) &&
                    EntityTagHeaderValue.TryParse(etag, out var tag))
                {
                    request.Headers.IfNoneMatch.Add(tag);
                }

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotModified) continue;
                response.EnsureSuccessStatusCode();

                await DownloadAsync(response, temp, fileName, progress, ct);

                // Same checks as a manual import, before the file is used anywhere.
                var signature = WinTrust.Check(temp);
                if (signature != SignatureCheck.AmdVerified)
                    throw new InvalidDataException($"{fileName} failed the AMD signature check, so it wasn't used.");
                var version = Fsr4Library.ReadFileVersion(temp);
                if (!Version.TryParse(version, out var v) || v < Fsr4Library.MinimumDx12Version)
                    throw new InvalidDataException($"{fileName} is version {version}, older than FSR 4.1.2, so it wasn't used.");

                File.Move(temp, target, overwrite: true);
                if (response.Headers.ETag is { } newTag) state.ETags[fileName] = newTag.ToString();
                changed = true;
                Log.Info($"Downloaded {fileName} {version} from {url}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                Log.Warn($"FSR 4 download failed for {fileName}: {ex.Message}");
                problem ??= ex is InvalidDataException
                    ? ex.Message
                    : "Couldn't download the FSR 4 files. Check the connection and press Refresh.";
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup only */ }
            }
        }

        if (problem is null) state.LastCheck = DateTime.UtcNow; // retry soon after a failure
        SaveState(state);
        return new Result(changed, problem);
    }

    private static async Task DownloadAsync(HttpResponseMessage response, string temp, string fileName,
        IProgress<string> progress, CancellationToken ct)
    {
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(temp);
        var buffer = new byte[81920];
        long done = 0;
        int read, lastPercent = -1;
        var label = fileName.Equals(InstallConfig.VulkanFsr4File, StringComparison.OrdinalIgnoreCase) ? "Vulkan" : "DX12";
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            var percent = total > 0 ? (int)(done * 100 / total) : -1;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress.Report(percent >= 0
                    ? $"Downloading FSR 4 files ({label})... {percent}%"
                    : $"Downloading FSR 4 files ({label})... {done / (1024 * 1024)} MB");
            }
        }
    }

    private static State LoadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State()
                : new State();
        }
        catch
        {
            return new State();
        }
    }

    private static void SaveState(State state)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(state)); }
        catch (Exception ex) { Log.Warn($"Couldn't save download state: {ex.Message}"); }
    }
}
