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

    // No spaces anywhere in this path: it's passed through Task Scheduler and PowerShell, where a space in
    // "ROG Ally Life" is the kind of thing that stops the installer from starting at all.
    private static string UpdateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ROGAllyLife", "FSR4LiteUpdate");

    /// <summary>Progress for the widget: a message, a percentage (null = indeterminate), and whether it failed.</summary>
    public sealed record UpdateProgress(string Message, int? Percent = null, bool Failed = false, bool ManualWindow = false);

    /// <summary>
    /// Downloads and unpacks a release, starts its installer outside the package, then follows the installer's own output
    /// (install.log) until it reports success or failure. The widget is closed by Windows when the new version installs,
    /// so a successful run usually ends with this process being stopped. If the background installer hasn't started
    /// within 20 seconds, the installer opens in its own window instead (Install.cmd through Explorer, also outside the
    /// package), so the update still happens.
    /// </summary>
    public static async Task RunUpdateAsync(string tag, Action<UpdateProgress> report)
    {
        var version = tag.TrimStart('v', 'V');
        var dir = Path.Combine(UpdateRoot, tag);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        // 1. Download, with a percentage.
        report(new UpdateProgress($"Downloading FSR 4.1 Lite {version}...", 0));
        var zip = Path.Combine(dir, "FSR4Lite_x64.zip");
        var url = $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/FSR4Lite_x64.zip";
        using (var response = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            var lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0)
                {
                    var percent = (int)(done * 100 / total);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        report(new UpdateProgress($"Downloading FSR 4.1 Lite {version}... {percent}%", percent));
                    }
                }
            }
        }

        // 2. Unpack and check it's a real release.
        report(new UpdateProgress("Preparing the update..."));
        var files = Path.Combine(dir, "files");
        ZipFile.ExtractToDirectory(zip, files);
        var installer = Path.Combine(files, "install.ps1");
        if (!File.Exists(installer) || !Directory.EnumerateFiles(files, "*.msix").Any())
            throw new InvalidDataException("The download doesn't look like an FSR 4.1 Lite release.");

        // 3. Start the installer in the background and follow its output.
        var log = Path.Combine(dir, "install.log");
        File.WriteAllText(Path.Combine(UpdateRoot, "pending.txt"), tag + "\n" + log);
        report(new UpdateProgress("Starting the installer..."));
        StartAsScheduledTask(dir, installer, log);
        Log.Info($"Widget update {tag} started; installer log: {log}");

        var started = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(1000);
            var text = ReadShared(log);

            if (text.Length == 0)
            {
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(20))
                {
                    // The background route didn't start: open the installer in its own window instead.
                    Log.Warn("Background installer didn't start within 20 s; opening Install.cmd instead");
                    StartInstallerWindow(files);
                    report(new UpdateProgress("Finishing the update in the installer window. The widget closes while it " +
                                              "installs; reopen it from Game Bar afterwards.", ManualWindow: true));
                    return;
                }
                continue;
            }

            var failed = text.IndexOf("Install failed", StringComparison.OrdinalIgnoreCase);
            if (failed >= 0)
            {
                var reason = text[failed..].Split('\n')[0].Replace("Install failed:", "").Trim();
                throw new InvalidOperationException(reason.Length > 0 ? reason : "The installer reported an error.");
            }
            if (text.Contains("Installed", StringComparison.Ordinal))
                report(new UpdateProgress($"FSR 4.1 Lite {version} is installed. The widget restarts now; reopen it from Game Bar."));
            else if (text.Contains("> Installing the widget", StringComparison.Ordinal))
                report(new UpdateProgress($"Installing FSR 4.1 Lite {version}. The widget closes for a moment..."));
            else if (text.Contains("> Installing runtime packages", StringComparison.Ordinal))
                report(new UpdateProgress("Checking the components the widget needs..."));
            else if (text.Contains("administrator permission", StringComparison.OrdinalIgnoreCase))
                report(new UpdateProgress("Approve the Windows prompt to trust the ROG Ally Life certificate..."));
            else
                report(new UpdateProgress("Checking the signing certificate..."));

            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(3))
                throw new TimeoutException("The installer is taking too long. Try again, or install the zip from the Releases page.");
        }
    }

    /// <summary>
    /// Opens the installer in its own window through Explorer, so it runs outside the widget's package. Uses Update.cmd,
    /// which (unlike Install.cmd, made for manual installs) doesn't wait for Enter, so the window closes by itself.
    /// </summary>
    private static void StartInstallerWindow(string files)
    {
        var cmd = Path.Combine(files, "Update.cmd");
        File.WriteAllText(cmd,
            "@echo off\r\n" +
            "title FSR 4.1 Lite update\r\n" +
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"\r\n");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{cmd}\"")
        {
            UseShellExecute = true,
        });
    }

    private static string ReadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
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

    private const string TaskName = @"ROGAllyLife\FSR4LiteUpdate"; // no spaces, like the update folder

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
            $"'Starting the installer' | Out-File -FilePath {Quote(log)} -Encoding utf8\r\n" +
            $"& {Quote(installer)} *>&1 | Out-File -FilePath {Quote(log)} -Encoding utf8 -Append\r\n" +
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
