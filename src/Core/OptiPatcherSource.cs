using System.Security.Cryptography;
using System.Text.Json;

namespace RalFsrHelper.Core;

/// <summary>OptiPatcher.asi, cached locally, and the release it came from (e.g. "v0.42").</summary>
public sealed record OptiPatcherFile(string Tag, string AsiPath);

/// <summary>One downloadable OptiPatcher release: its tag, the .asi's address, size and SHA-256 (null if GitHub gave none).</summary>
public sealed record OptiPatcherRelease(string Tag, string Url, long Size, string? Sha256, DateTimeOffset Published);

/// <summary>
/// OptiPatcher (github.com/optiscaler/OptiPatcher, MIT), OptiScaler's official ASI plugin. In the games OptiScaler's
/// Compatibility List marks for it, it makes the game show DLSS on AMD without spoofing an NVIDIA card. DOOM Eternal's
/// wiki page: "For AMD/Intel, requires using OptiPatcher to expose DLSS setting". Without it DLSS stays greyed out and
/// FSR 4.1 Lite never runs there.
///
/// Downloaded the first time a game needs it: the newest published release through GitHub's API, checked against the
/// asset's SHA-256 digest, or the pinned v0.42 when the API can't be reached. Once one is here, a newer release is looked
/// for at most once a day. Nothing from OptiPatcher ships inside the widget package.
/// </summary>
public static class OptiPatcherSource
{
    public const string ApiUrl = "https://api.github.com/repos/optiscaler/OptiPatcher/releases?per_page=10";
    public const string DownloadPrefix = "https://github.com/optiscaler/OptiPatcher/releases/download/";
    private const long MaxAsi = 16L << 20;
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    /// <summary>The release used when GitHub's API can't be reached.</summary>
    public static readonly OptiPatcherRelease PinnedRelease = new("v0.42",
        DownloadPrefix + "v0.42/OptiPatcher_v0.42.asi", 107520,
        "2a6e033aeda72d63281889480b2380da830530cf6e1e448e2056d451ade75f1b", default);

    /// <summary>The fallback in use: <see cref="PinnedRelease"/>, or a stand-in in tests.</summary>
    public static OptiPatcherRelease Pinned { get; set; } = PinnedRelease;

    /// <summary>The cache folder. Settable for tests.</summary>
    public static string CacheDir { get; set; } = Path.Combine(AppPaths.Root, "optipatcher");

    /// <summary>Replaces the network for tests.</summary>
    public static HttpMessageHandler? Handler { get; set; }

    private static string AsiPath => Path.Combine(CacheDir, "OptiPatcher.asi");
    private static string TagFile => Path.Combine(CacheDir, "latest.txt");
    private static string LastCheckFile => Path.Combine(CacheDir, "last-check.txt");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static HttpClient Client()
    {
        var c = Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
        c.Timeout = TimeSpan.FromMinutes(2);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RALFsr4Lite/1.0");
        return c;
    }

    /// <summary>The OptiPatcher ready to install, or null.</summary>
    public static OptiPatcherFile? Cached()
    {
        try
        {
            if (!File.Exists(AsiPath) || !File.Exists(TagFile)) return null;
            var tag = File.ReadAllText(TagFile).Trim();
            return tag.Length > 0 ? new OptiPatcherFile(tag, AsiPath) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The newest OptiPatcher in GitHub's release list: the .asi of the latest published release that isn't a draft or a
    /// pre-release (the "rolling" tag is an ordinary release and loses on its older date). Null when there's none.
    /// </summary>
    public static OptiPatcherRelease? Newest(string json)
    {
        OptiPatcherRelease? best = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object) continue;
                if (Bool(release, "draft") || Bool(release, "prerelease")) continue;
                var tag = Str(release, "tag_name");
                if (tag.Length == 0) continue;
                if (!DateTimeOffset.TryParse(Str(release, "published_at"), out var published)) continue;
                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (!name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) || name.Contains('/')) continue;
                    if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                    var state = Str(a, "state");
                    if (state.Length > 0 && state != "uploaded") continue;
                    if (!a.TryGetProperty("size", out var s) || !s.TryGetInt64(out var size) || size <= 0 || size > MaxAsi) continue;
                    var digest = Str(a, "digest");
                    var sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                    if (best is null || published > best.Published) best = new OptiPatcherRelease(tag, url, size, sha, published);
                    break;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return best;

        static bool Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        static string Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>
    /// OptiPatcher for a game that needs it: the cached one, else the newest release, else the pinned v0.42.
    /// Null when nothing could be downloaded (the game is then set up without it and gets it later). Never throws.
    /// </summary>
    public static async Task<OptiPatcherFile?> ForGameAsync()
    {
        if (Cached() is { } cached) return cached;
        await Gate.WaitAsync();
        try
        {
            if (Cached() is { } now) return now;
            Directory.CreateDirectory(CacheDir);
            try
            {
                var newest = await NewestFromGitHubAsync();
                File.WriteAllText(LastCheckFile, DateTime.UtcNow.ToString("O"));
                return await DownloadAsync(newest);
            }
            catch (Exception ex)
            {
                Log.Warn($"OptiPatcher's newest release couldn't be used ({ex.Message}); using {Pinned.Tag}");
                return await DownloadAsync(Pinned);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't get OptiPatcher: {ex.Message}");
            return Cached();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Looks for a newer OptiPatcher at most once a day, once one is here (nothing is downloaded before a game needs it).
    /// Returns the OptiPatcher to use, or null when there's none. Never throws.
    /// </summary>
    public static async Task<OptiPatcherFile?> UpdateAsync(DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        await Gate.WaitAsync();
        try
        {
            var cached = Cached();
            if (cached is null) return null;
            if (File.Exists(LastCheckFile) &&
                DateTime.TryParse(File.ReadAllText(LastCheckFile).Trim(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var checkedAt) &&
                now - checkedAt.ToUniversalTime() is var age && age >= TimeSpan.Zero && age < CheckEvery)
                return cached;

            var newest = await NewestFromGitHubAsync();
            File.WriteAllText(LastCheckFile, now.ToString("O"));
            if (newest.Tag.Equals(cached.Tag, StringComparison.Ordinal)) return cached;
            return await DownloadAsync(newest);
        }
        catch (Exception ex)
        {
            Log.Warn($"OptiPatcher check: {ex.Message}");
            return Cached();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<OptiPatcherRelease> NewestFromGitHubAsync()
    {
        using var http = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return Newest(await response.Content.ReadAsStringAsync())
               ?? throw new InvalidDataException("No OptiPatcher in OptiPatcher's releases");
    }

    /// <summary>Downloads one release, checks size, SHA-256 and that it's a Windows DLL, then makes it the cached one.</summary>
    private static async Task<OptiPatcherFile> DownloadAsync(OptiPatcherRelease release)
    {
        using var http = Client();
        var bytes = await http.GetByteArrayAsync(release.Url);
        if (bytes.Length != release.Size)
            throw new InvalidDataException($"OptiPatcher {release.Tag} is {bytes.Length} bytes, expected {release.Size}");
        if (release.Sha256 is { } expected &&
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"OptiPatcher {release.Tag} doesn't match GitHub's SHA-256");
        if (bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidDataException("OptiPatcher's download isn't a Windows plugin");

        Directory.CreateDirectory(CacheDir);
        var temp = AsiPath + ".new";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, AsiPath, overwrite: true);
        File.WriteAllText(TagFile, release.Tag);
        Log.Info($"OptiPatcher {release.Tag} ready");
        return new OptiPatcherFile(release.Tag, AsiPath);
    }
}
