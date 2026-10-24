using System.Net;

namespace RalFsrHelper.Core;

/// <summary>
/// Downloads the FSR 4.1.2 driver files so users never handle them by hand, from the fsr4/ folder of ROG Ally Life's
/// R2 bucket through its r2.dev address. (The media.rogallylife.com custom domain sits behind the zone's bot
/// protection, which refuses the helper.)
///
/// Once a day the helper reads the files' ETags with two HEAD requests and downloads only if they changed, so
/// uploading new DLLs over the old ones in fsr4/ updates every user.
///
/// Each DLL is used only after the same checks as a manual import: a valid AMD or WHQL signature on an AMD driver
/// file, at least version 2.4.0.4370. Nothing replaces the current files unless every downloaded file passes.
/// </summary>
public static class Fsr4Download
{
    private const string R2Base = R2Files.Fsr4Folder;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static string StatePath => Path.Combine(AppPaths.Fsr4Dir, "download-source.txt");
    private static string CheckPath => Path.Combine(AppPaths.Fsr4Dir, "download-checked.txt");

    private static readonly HttpClient Http = Create(followRedirects: true, TimeSpan.FromMinutes(30));
    private static readonly HttpClient NoRedirect = Create(followRedirects: false, TimeSpan.FromSeconds(20));

    private static HttpClient Create(bool followRedirects, TimeSpan timeout)
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = followRedirects }) { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RALFsr4Lite/0.1");
        return c;
    }

    public sealed record Result(bool Changed, string? Problem);

    public static async Task<Result> EnsureAsync(IProgress<string> progress, bool force = false, CancellationToken ct = default)
    {
        AppPaths.Ensure();
        var dx12 = Path.Combine(AppPaths.Fsr4Dir, InstallConfig.Dx12Fsr4File);
        var haveFiles = File.Exists(dx12);
        if (haveFiles && !force && DateTime.UtcNow - ReadTime(CheckPath) < CheckInterval) return new Result(false, null);

        var temp = Path.Combine(Path.GetTempPath(), "RalFsr4Download");
        var recorded = ReadText(StatePath);

        try
        {
            // Cheap fingerprint first: if it matches what we already have, there's nothing to download.
            var fingerprint = await FingerprintAsync(ct);
            if (haveFiles && fingerprint is not null && fingerprint == recorded)
            {
                WriteText(CheckPath, DateTime.UtcNow.ToString("o"));
                return new Result(false, null);
            }

            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            var unpacked = Path.Combine(temp, "files");
            Directory.CreateDirectory(unpacked);
            await FetchAsync(unpacked, progress, ct);

            progress.Report("Checking the FSR 4 files...");
            var accepted = Validate(unpacked);

            // Every file passed: only now replace the current ones.
            foreach (var (file, name) in accepted)
                File.Copy(file, Path.Combine(AppPaths.Fsr4Dir, name), overwrite: true);

            if (fingerprint is not null) WriteText(StatePath, fingerprint);
            WriteText(CheckPath, DateTime.UtcNow.ToString("o"));
            Log.Info($"FSR 4 files updated from R2: {string.Join(", ", accepted.Select(a => a.Name))}");
            return new Result(true, null);
        }
        catch (Exception ex)
        {
            Log.Error("FSR 4 download failed", ex);
            return new Result(false, ex is InvalidDataException
                ? ex.Message
                : $"Couldn't download the FSR 4 files ({OptiScalerSource.Describe(ex)}).");
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { /* temp cleanup only */ }
        }
    }

    private static readonly string[] Files = { InstallConfig.Dx12Fsr4File, InstallConfig.VulkanFsr4File };

    /// <summary>Identifies the files currently on the host without downloading them.</summary>
    private static async Task<string?> FingerprintAsync(CancellationToken ct)
    {
        var tags = new List<string>();
        foreach (var name in Files)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, R2Base + name);
            using var response = await NoRedirect.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"R2 answered HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {name}", null, response.StatusCode);
            tags.Add(response.Headers.ETag?.Tag ?? response.Content.Headers.ContentLength?.ToString() ?? "?");
        }
        return "r2:" + string.Join("|", tags);
    }

    /// <summary>Downloads the host's files and leaves the DLLs in <paramref name="unpacked"/>.</summary>
    private static async Task FetchAsync(string unpacked, IProgress<string> progress, CancellationToken ct)
    {
        foreach (var name in Files)
        {
            var label = name == InstallConfig.VulkanFsr4File ? "Vulkan " : "DX12 ";
            await DownloadAsync(R2Base + name, Path.Combine(unpacked, name), label, progress, ct);
        }
    }

    private static List<(string File, string Name)> Validate(string unpacked)
    {
        var accepted = new List<(string, string)>();
        foreach (var name in Files)
        {
            var file = Directory.GetFiles(unpacked, name, SearchOption.AllDirectories).FirstOrDefault();
            if (file is null)
            {
                if (name == InstallConfig.Dx12Fsr4File)
                    throw new InvalidDataException("The download doesn't contain amdxcffx64.dll.");
                continue; // the Vulkan file is optional
            }

            if (WinTrust.Check(file) != SignatureCheck.AmdVerified)
                throw new InvalidDataException($"{name} failed the AMD signature check, so the download wasn't used.");
            var version = Fsr4Library.ReadFileVersion(file);
            if (!Version.TryParse(version, out var v) || v < Fsr4Library.MinimumDx12Version)
                throw new InvalidDataException($"{name} is version {version}, older than FSR 4.1.2, so the download wasn't used.");
            accepted.Add((file, name));
        }
        return accepted;
    }

    private static async Task DownloadAsync(string url, string target, string label, IProgress<string> progress,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{new Uri(url).Host} answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        var total = response.Content.Headers.ContentLength ?? 0;
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
            progress.Report(percent >= 0
                ? $"Downloading FSR 4 {label}files... {percent}%"
                : $"Downloading FSR 4 {label}files... {done / (1024 * 1024)} MB");
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
