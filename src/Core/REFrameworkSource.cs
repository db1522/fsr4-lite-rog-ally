using System.IO.Compression;
using System.Net;

namespace RalFsrHelper.Core;

/// <summary>REFramework's dinput8.dll, cached locally, and the nightly it came from (e.g. "nightly-01389-1c3d5b1").</summary>
public sealed record RefPackage(string Version, string DllPath)
{
    public string DisplayName => "REFramework " + (Version.StartsWith("nightly-", StringComparison.OrdinalIgnoreCase)
        ? "nightly " + Version.Split('-')[1]
        : Version);
}

/// <summary>
/// REFramework (praydog) for Capcom RE Engine games whose OptiScaler Compatibility List entry says "Requires REFramework"
/// (Monster Hunter Wilds, Onimusha, PRAGMATA, Resident Evil 9 Requiem...). REFramework hooks the engine early and gets past
/// Capcom's integrity checks so OptiScaler can work. One build, REFramework.zip, covers every supported RE Engine game;
/// only its dinput8.dll is used, as REFramework's own release notes say for non-VR use.
///
/// Downloaded from the official praydog/REFramework-nightly releases through the permanent latest/download link (no
/// GitHub API, so no rate limit), at most once a day. The first redirect names the nightly's tag, which is how a new
/// build is recognised without downloading the zip again.
/// </summary>
public static class REFrameworkSource
{
    private const string LatestZip = "https://github.com/praydog/REFramework-nightly/releases/latest/download/REFramework.zip";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private static string CacheDir => Path.Combine(AppPaths.Root, "reframework");
    private static string DllPath => Path.Combine(CacheDir, "dinput8.dll");
    private static string VersionFile => Path.Combine(CacheDir, "version.txt");
    private static string LastCheckFile => Path.Combine(CacheDir, "last-check.txt");

    private static readonly HttpClient Probe = Client(redirects: false);
    private static readonly HttpClient Download = Client(redirects: true);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static HttpClient Client(bool redirects)
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RALFsr4Lite/1.0");
        return c;
    }

    public static RefPackage? Cached()
    {
        try
        {
            return File.Exists(DllPath) && File.Exists(VersionFile)
                ? new RefPackage(File.ReadAllText(VersionFile).Trim(), DllPath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The newest REFramework, downloading it when the cache is missing or a day old. Never throws.</summary>
    public static async Task<RefPackage?> EnsureLatestAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var cached = Cached();
            if (cached is not null && File.Exists(LastCheckFile) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(LastCheckFile) < CheckEvery)
                return cached;

            // The latest/download link redirects to .../releases/download/<tag>/REFramework.zip.
            using var probe = await Probe.GetAsync(LatestZip, HttpCompletionOption.ResponseHeadersRead);
            var location = probe.Headers.Location;
            if (probe.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.MovedPermanently)
                || location is null)
            {
                Log.Warn($"REFramework check: unexpected response {(int)probe.StatusCode}");
                return cached;
            }
            if (!location.IsAbsoluteUri) location = new Uri(new Uri(LatestZip), location);
            var segments = location.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var tag = segments.Length >= 2 ? segments[^2] : "unknown";

            Directory.CreateDirectory(CacheDir);
            if (cached is not null && cached.Version.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(LastCheckFile, DateTime.UtcNow.ToString("O"));
                return cached;
            }

            // Download and keep only dinput8.dll.
            var zipPath = Path.Combine(CacheDir, "REFramework.zip");
            using (var response = await Download.GetAsync(location, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zipPath);
                await response.Content.CopyToAsync(file);
            }

            var temp = DllPath + ".new";
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("REFramework.zip has no dinput8.dll");
                entry.ExtractToFile(temp, overwrite: true);
            }
            File.Move(temp, DllPath, overwrite: true);
            File.Delete(zipPath);
            File.WriteAllText(VersionFile, tag);
            File.WriteAllText(LastCheckFile, DateTime.UtcNow.ToString("O"));
            Log.Info($"REFramework {tag} ready");
            return new RefPackage(tag, DllPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't get REFramework: {ex.Message}");
            return Cached();
        }
        finally
        {
            Gate.Release();
        }
    }
}
