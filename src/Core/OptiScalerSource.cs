using System.Text.Json;

namespace RalFsrHelper.Core;

public sealed record OptiPackage(string Build, string Dir)
{
    /// <summary>"OptiScaler v0.10.0-pre1_20261024" style label for the widget.</summary>
    public string DisplayName =>
        Build.StartsWith("OptiScaler_", StringComparison.OrdinalIgnoreCase) ? "OptiScaler " + Build["OptiScaler_".Length..] : Build;
}

/// <summary>
/// Downloads OptiScaler straight from its official nightly builds (the v10 line the FSR 4.1.2 files are built for),
/// so users get each new nightly automatically. Nightlies live in the separate optiscaler/OptiScaler-nightly
/// repository, one release per day (tags like nightly-20261024, assets like OptiScaler_v10.0.0-pre1_20261024.7z).
/// The "nightly" release in the main repository is now only a pointer there.
///
/// GitHub allows 60 unauthenticated API requests per hour per connection. Once a build is downloaded, the release is
/// checked for a newer nightly at most once a day, and the helper retries a failed check at most once a minute.
/// Existing game installs keep the build they were installed with until they're reinstalled.
/// </summary>
public static class OptiScalerSource
{
    private const string NightlyApi = "https://api.github.com/repos/optiscaler/OptiScaler-nightly/releases?per_page=10";
    private static readonly string LatestMarker = Path.Combine(AppPaths.OptiCache, "latest.txt");
    private static readonly string LastCheckMarker = Path.Combine(AppPaths.OptiCache, "last-check.txt");
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RALFsr4Lite/0.1");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Returns the newest nightly, downloading it if needed. Falls back to the cached build when offline.</summary>
    public static async Task<OptiPackage?> EnsureLatestAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        AppPaths.Ensure();

        var cached = Cached();
        if (cached is not null && DateTime.UtcNow - ReadTime(LastCheckMarker) < CheckInterval) return cached;

        try
        {
            progress.Report("Checking for the latest OptiScaler nightly...");
            var asset = await GetLatestAssetAsync(ct);
            WriteText(LastCheckMarker, DateTime.UtcNow.ToString("o"));
            if (asset is null) throw new InvalidDataException("no OptiScaler build in the recent nightly releases");

            var build = Path.GetFileNameWithoutExtension(asset.Value.Name);
            if (cached is not null && cached.Build == build) return cached;

            var dir = Path.Combine(AppPaths.OptiCache, build);
            var existing = FindPackageRoot(dir);
            if (existing is not null)
            {
                WriteText(LatestMarker, build);
                return new OptiPackage(build, existing);
            }

            var archive = Path.Combine(AppPaths.OptiCache, asset.Value.Name);
            await DownloadAsync(asset.Value.Url, archive, asset.Value.Size, progress, ct);

            progress.Report("Unpacking OptiScaler...");
            var temp = dir + ".tmp";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            await Task.Run(() => ArchiveUtil.ExtractAll(archive, temp), ct);
            File.Delete(archive);
            if (FindPackageRoot(temp) is null)
            {
                Directory.Delete(temp, true);
                throw new InvalidDataException("the download doesn't contain OptiScaler.dll");
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(temp, dir);

            WriteText(LatestMarker, build);
            DeleteOldBuilds(keep: build);
            var package = new OptiPackage(build, FindPackageRoot(dir)!);
            Log.Info($"OptiScaler {build} ready at {package.Dir}");
            return package;
        }
        catch (Exception ex)
        {
            Log.Error("OptiScaler download failed", ex);
            progress.Report(cached is null
                ? $"Couldn't download OptiScaler ({Describe(ex)})."
                : $"Couldn't check for a new OptiScaler build ({Describe(ex)}). Using {cached.DisplayName}.");
            return cached;
        }
    }

    public static OptiPackage? Cached()
    {
        try
        {
            var build = ReadText(LatestMarker);
            if (string.IsNullOrEmpty(build)) return null;
            var root = FindPackageRoot(Path.Combine(AppPaths.OptiCache, build));
            return root is null ? null : new OptiPackage(build, root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Short, readable reason for the widget's status line.</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code} {code}",
        HttpRequestException h when h.InnerException is { } inner => inner.Message,
        HttpRequestException h => h.Message,
        TaskCanceledException => "the download timed out",
        _ => ex.Message,
    };

    private static async Task<(string Name, string Url, long Size)?> GetLatestAssetAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync(NightlyApi, ct);
        if ((int)response.StatusCode is 403 or 429)
            throw new HttpRequestException("GitHub's request limit was reached, try again in an hour");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        // A list of releases, newest first. Look through all of them for the newest usable build, in case the
        // latest release is still uploading or only has a debug build.
        var releases = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };

        (string Name, string Url, long Size, DateTime Created)? best = null;
        foreach (var release in releases)
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("assets", out var assets)) continue;

            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("debug", StringComparison.OrdinalIgnoreCase)) continue;
                if (a.TryGetProperty("state", out var state) && state.GetString() != "uploaded") continue;

                var created = a.GetProperty("created_at").GetDateTime();
                if (best is null || created > best.Value.Created)
                    best = (name, a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64(), created);
            }
        }
        return best is null ? null : (best.Value.Name, best.Value.Url, best.Value.Size);
    }

    private static async Task DownloadAsync(string url, string target, long size, IProgress<string> progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

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
            var percent = total > 0 ? (int)(done * 100 / total) : -1;
            if (percent == lastPercent) continue;
            lastPercent = percent;
            progress.Report(percent >= 0 ? $"Downloading OptiScaler... {percent}%" : $"Downloading OptiScaler... {done / (1024 * 1024)} MB");
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

    /// <summary>Removes older downloaded builds. Games keep their own copies, so nothing in use is affected.</summary>
    private static void DeleteOldBuilds(string keep)
    {
        foreach (var dir in Directory.GetDirectories(AppPaths.OptiCache))
        {
            if (Path.GetFileName(dir).Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static DateTime ReadTime(string path) =>
        DateTime.TryParse(ReadText(path), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;

    private static void WriteText(string path, string text)
    {
        try { File.WriteAllText(path, text); }
        catch (Exception ex) { Log.Warn($"Couldn't save {Path.GetFileName(path)}: {ex.Message}"); }
    }
}
