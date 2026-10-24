using System.Net.Http.Headers;
using System.Text.Json;

namespace RalFsrHelper.Core;

public sealed record OptiPackage(string Build, string Dir);

/// <summary>
/// Downloads OptiScaler straight from its official GitHub "nightly" release (the v10 line the FSR 4.1.2 files are
/// built for). The app never hosts or redistributes OptiScaler itself.
/// </summary>
public static class OptiScalerSource
{
    private const string NightlyApi = "https://api.github.com/repos/optiscaler/OptiScaler/releases/tags/nightly";
    private static readonly string LatestMarker = Path.Combine(AppPaths.OptiCache, "latest.txt");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RALFsrHelper", "0.1"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Returns the newest nightly, downloading it if needed. Falls back to the last cached build when offline.</summary>
    public static async Task<OptiPackage?> EnsureLatestAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        AppPaths.Ensure();
        try
        {
            progress.Report("Checking for the latest OptiScaler nightly...");
            var asset = await GetLatestAssetAsync(ct);
            if (asset is null)
            {
                progress.Report("No OptiScaler nightly build found on GitHub.");
                return Cached();
            }

            var build = Path.GetFileNameWithoutExtension(asset.Value.Name);
            var dir = Path.Combine(AppPaths.OptiCache, build);
            var existing = FindPackageRoot(dir);
            if (existing is not null)
            {
                await File.WriteAllTextAsync(LatestMarker, build, ct);
                return new OptiPackage(build, existing);
            }

            var archive = Path.Combine(AppPaths.OptiCache, asset.Value.Name);
            await DownloadAsync(asset.Value.Url, archive, asset.Value.Size, progress, ct);

            progress.Report("Unpacking OptiScaler...");
            var temp = dir + ".tmp";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            await Task.Run(() => ArchiveUtil.ExtractAll(archive, temp), ct);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(temp, dir);
            File.Delete(archive);

            var root = FindPackageRoot(dir) ?? throw new InvalidDataException("The download doesn't contain OptiScaler.dll.");
            await File.WriteAllTextAsync(LatestMarker, build, ct);
            Log.Info($"OptiScaler {build} ready at {root}");
            return new OptiPackage(build, root);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn($"OptiScaler download failed: {ex.Message}");
            var cached = Cached();
            progress.Report(cached is null
                ? "Couldn't reach GitHub to download OptiScaler. Check the connection and try again."
                : $"Offline. Using the last downloaded build ({cached.Build}).");
            return cached;
        }
    }

    public static OptiPackage? Cached()
    {
        try
        {
            if (!File.Exists(LatestMarker)) return null;
            var build = File.ReadAllText(LatestMarker).Trim();
            var root = FindPackageRoot(Path.Combine(AppPaths.OptiCache, build));
            return root is null ? null : new OptiPackage(build, root);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string Name, string Url, long Size)?> GetLatestAssetAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync(NightlyApi, ct);
        if ((int)response.StatusCode == 403)
            throw new HttpRequestException("GitHub rate limit reached. Try again in an hour.");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        (string Name, string Url, long Size, DateTime Created)? best = null;

        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("debug", StringComparison.OrdinalIgnoreCase)) continue;

            var created = a.GetProperty("created_at").GetDateTime();
            if (best is null || created > best.Value.Created)
            {
                best = (name, a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64(), created);
            }
        }

        return best is null ? null : (best.Value.Name, best.Value.Url, best.Value.Size);
    }

    private static async Task DownloadAsync(string url, string target, long size, IProgress<string> progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? size;

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(target);
        var buffer = new byte[81920];
        long done = 0;
        int read, lastPercent = -1;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                var percent = (int)(done * 100 / total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report($"Downloading OptiScaler... {percent}%");
                }
            }
        }
    }

    /// <summary>The folder holding OptiScaler.dll, at the top of the package or one level down.</summary>
    private static string? FindPackageRoot(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        if (File.Exists(Path.Combine(dir, "OptiScaler.dll"))) return dir;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (File.Exists(Path.Combine(sub, "OptiScaler.dll"))) return sub;
        }
        return null;
    }
}
