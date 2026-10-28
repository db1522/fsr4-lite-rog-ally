using System.IO.Compression;
using System.Net;
using RalFsrHelper.Core;

namespace RalFsrHelper.Host;

/// <summary>
/// Updates the widget itself from the latest GitHub release.
///
/// Checking: github.com/{repo}/releases/latest redirects to .../releases/tag/v1.0.10, so the version comes from the
/// redirect, with no GitHub API call (no rate limit). Checked at most every 6 hours.
///
/// Installing: the release's FSR4Lite_x64.zip is unpacked into ProgramData (outside AppData, so it isn't redirected into
/// the package's private storage) and its own install.ps1 runs, the same installer as Install.cmd. Installing the update
/// closes the widget and helper, so the installer can't be their child process: it runs as a one-off Windows scheduled
/// task, which belongs to the Task Scheduler service, then deletes itself. Its output goes to install.log next to it.
/// </summary>
public static class SelfUpdate
{
    public const string Repo = "db1522/fsr4-lite-rog-ally";
    private const string LatestUrl = "https://github.com/" + Repo + "/releases/latest";
    /// <summary>Background check interval (the helper can run for days as the notification watcher).</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    /// <summary>When the widget is opened: look again if the last check is older than this.</summary>
    public static readonly TimeSpan CheckOnOpenEvery = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Probe = Client(redirects: false);
    private static readonly HttpClient Download = Client(redirects: true);
    private static HttpClient Client(bool redirects)
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RALFsr4Lite/1.0");
        return c;
    }

    private static DateTime _checkedAt = DateTime.MinValue;
    private static (string Tag, Version Version)? _latest;

    /// <summary>The installed widget's version, from its package.</summary>
    public static Version Installed
    {
        get
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return new Version(v.Major, v.Minor, v.Build, v.Revision);
            }
            catch
            {
                return new Version(0, 0, 0, 0);
            }
        }
    }

    public static string Display(Version v) => v.Revision == 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : v.ToString();

    /// <summary>The newest release when it's newer than the installed widget, else null. Never throws.</summary>
    public static async Task<(string Tag, Version Version)?> CheckAsync(bool force = false, TimeSpan? maxAge = null)
    {
        try
        {
            if (force || DateTime.UtcNow - _checkedAt > (maxAge ?? CheckEvery))
            {
                _checkedAt = DateTime.UtcNow;
                using var response = await Probe.GetAsync(LatestUrl, HttpCompletionOption.ResponseHeadersRead);
                var location = response.Headers.Location?.ToString() ?? "";
                var tag = location.Contains("/releases/tag/") ? location[(location.LastIndexOf('/') + 1)..] : "";
                if (TryParse(tag, out var version)) _latest = (Uri.UnescapeDataString(tag), version);
                else Log.Warn($"Widget update check: no release tag in the redirect ({(int)response.StatusCode})");
            }
            return _latest is { } l && l.Version > Installed ? l : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Widget update check failed: {ex.Message}");
            return null;
        }
    }

    private static bool TryParse(string tag, out Version version)
    {
        version = new Version(0, 0);
        var text = tag.TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) text = text[..cut];
        if (!Version.TryParse(text, out var v)) return false;
        version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        return true;
    }

    private static string UpdateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ROG Ally Life", "FSR4Lite-update");

    /// <summary>Downloads and unpacks a release, then starts its installer outside the package. Returns the log path.</summary>
    public static async Task<string> DownloadAndStartAsync(string tag, IProgress<string> progress)
    {
        var dir = Path.Combine(UpdateRoot, tag);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        progress.Report($"Downloading FSR 4.1 Lite {tag.TrimStart('v')}...");
        var zip = Path.Combine(dir, "FSR4Lite_x64.zip");
        var url = $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/FSR4Lite_x64.zip";
        using (var response = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(zip);
            await response.Content.CopyToAsync(file);
        }

        progress.Report("Unpacking the update...");
        var files = Path.Combine(dir, "files");
        ZipFile.ExtractToDirectory(zip, files);
        var installer = Path.Combine(files, "install.ps1");
        if (!File.Exists(installer) || !Directory.EnumerateFiles(files, "*.msix").Any())
            throw new InvalidDataException("The download doesn't look like an FSR 4.1 Lite release.");

        var log = Path.Combine(dir, "install.log");
        File.WriteAllText(Path.Combine(UpdateRoot, "pending.txt"), tag + "\n" + log);
        progress.Report("Installing. The widget closes for a moment; reopen it from Game Bar.");

        StartAsScheduledTask(dir, installer, log);
        Log.Info($"Widget update {tag} started; installer log: {log}");
        return log;
    }

    /// <summary>
    /// After an update: "Updated to 1.0.10", or why it failed, once. Null when no update was pending.
    /// </summary>
    public static string? TakeResult()
    {
        try
        {
            var pending = Path.Combine(UpdateRoot, "pending.txt");
            if (!File.Exists(pending)) return null;
            var lines = File.ReadAllLines(pending);
            var tag = lines.ElementAtOrDefault(0) ?? "";
            var log = lines.ElementAtOrDefault(1) ?? "";
            if (TryParse(tag, out var target) && Installed >= target)
            {
                File.Delete(pending);
                TryDelete(Path.Combine(UpdateRoot, tag));
                return $"Updated to FSR 4.1 Lite {Display(target)}.";
            }

            var text = File.Exists(log) ? File.ReadAllText(log) : "";
            var failed = text.Contains("Install failed", StringComparison.OrdinalIgnoreCase);
            if (!failed) return null; // still installing, or the installer hasn't written anything yet
            File.Delete(pending);
            var reason = text[text.IndexOf("Install failed", StringComparison.OrdinalIgnoreCase)..].Split('\n')[0].Trim();
            return $"The update to {tag.TrimStart('v')} didn't install. {reason}";
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    // ---- Running the installer outside the widget: a one-off scheduled task ----

    private const string TaskName = @"ROG Ally Life\FSR 4.1 Lite update";

    /// <summary>
    /// Writes run-update.ps1 (runs install.ps1 hidden, logs its output, deletes the task) and starts it as a scheduled
    /// task for the signed-in user. Windows still shows its admin prompt if the update's certificate needs trusting.
    /// </summary>
    private static void StartAsScheduledTask(string dir, string installer, string log)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var script = Path.Combine(dir, "run-update.ps1");
        File.WriteAllText(script,
            "$ErrorActionPreference = 'Continue'\r\n" +
            $"& {Quote(installer)} *>&1 | Out-File -FilePath {Quote(log)} -Encoding utf8\r\n" +
            $"schtasks.exe /Delete /TN {Quote(TaskName)} /F | Out-Null\r\n");

        var action = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"";
        RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"{action.Replace("\"", "\\\"")}\" /SC ONCE /ST 00:00 /RL LIMITED /F");
        RunSchtasks($"/Run /TN \"{TaskName}\"");
    }

    private static void RunSchtasks(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start schtasks.exe");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"Couldn't schedule the update installer: {output.Trim()}");
    }
}
