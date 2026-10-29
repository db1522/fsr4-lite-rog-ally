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
/// closes the widget and helper, so the installer can't be their child process: Explorer starts it, minimized, through
/// Update.cmd, which logs its output to install.log next to it and closes itself.
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

        // An install of this version started in the last few minutes is still running (the widget reopened during it
        // and offered the update again): don't start a second one.
        if (InstallInProgress(tag))
        {
            report(new UpdateProgress($"An update to FSR 4.1 Lite {version} is already installing. Game Bar " +
                                      "closes when it finishes; open it again afterwards."));
            return;
        }

        // A fresh folder for every attempt, so a second attempt can never collide with files the first is still using.
        var dir = Path.Combine(UpdateRoot, $"{tag}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);
        CleanOldAttempts(dir);

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

        // 3. Start the installer outside the widget and follow its output. Task Scheduler turned out not to run the
        //    installer on some devices (the task was created but never started), so the main route is the one that
        //    always worked: Explorer opens Update.cmd, minimized, which logs the installer's output and closes itself.
        var log = Path.Combine(dir, "install.log");
        File.WriteAllText(Path.Combine(UpdateRoot, "pending.txt"), tag + "\n" + log);
        // A short countdown, so Game Bar closing is never a surprise. The installer then shows Windows notifications
        // under the widget's name and reopens Game Bar by itself when it's done.
        for (var i = 3; i >= 1; i--)
        {
            report(new UpdateProgress($"Ready to install FSR 4.1 Lite {version}. Game Bar closes in {i}..."));
            await Task.Delay(1000);
        }
        report(new UpdateProgress("Installing. Game Bar closes now; tap the notification to open it again when the update is done."));
        StartInstallerWindow(files, log);
        Log.Info($"Widget update {tag} started; installer log: {log}");

        var started = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(1000);
            var text = ReadShared(log);

            if (text.Length == 0)
            {
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException("The installer didn't start. Try again, or install the zip from the Releases page.");
                continue;
            }

            var failed = text.IndexOf("Install failed", StringComparison.OrdinalIgnoreCase);
            if (failed >= 0)
            {
                var reason = text[failed..].Split('\n')[0].Replace("Install failed:", "").Trim();
                throw new InvalidOperationException(reason.Length > 0 ? reason : "The installer reported an error.");
            }
            if (text.Contains("Installed", StringComparison.Ordinal))
                report(new UpdateProgress($"FSR 4.1 Lite {version} is installed. Game Bar closes now; open it again to use the new version."));
            else if (text.Contains("> Installing the widget", StringComparison.Ordinal))
                report(new UpdateProgress($"Installing FSR 4.1 Lite {version}. Game Bar closes for a moment; open it again in a few seconds..."));
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
    /// Runs the installer outside the widget's package: Explorer opens Update.cmd (Explorer's child, not ours, so the
    /// install closing the widget can't stop it). Update.cmd starts itself again minimized, runs install.ps1 with its
    /// output going to install.log (which the widget follows for live progress), and closes itself. Unlike Install.cmd,
    /// made for manual installs, it never waits for Enter.
    /// </summary>
    private static void StartInstallerWindow(string files, string log)
    {
        var cmd = Path.Combine(files, "Update.cmd");
        File.WriteAllText(cmd,
            "@echo off\r\n" +
            "if not \"%~1\"==\"min\" (start \"FSR 4.1 Lite update\" /min \"%~f0\" min & exit /b)\r\n" +
            "title FSR 4.1 Lite update\r\n" +
            "set RAL_UPDATE=1\r\n" +
            "echo Updating FSR 4.1 Lite. This window closes by itself.\r\n" +
            // *>&1 merges every PowerShell stream (Write-Host included) into the output that goes to install.log.
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"& '%~dp0install.ps1' *>&1\" > \"{log}\" 2>&1\r\n" +
            "exit\r\n");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{cmd}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>True when pending.txt names this release and was written in the last 5 minutes.</summary>
    private static bool InstallInProgress(string tag)
    {
        try
        {
            var pending = Path.Combine(UpdateRoot, "pending.txt");
            if (!File.Exists(pending)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(pending) > TimeSpan.FromMinutes(5)) return false;
            var line = File.ReadLines(pending).FirstOrDefault() ?? "";
            return line.Equals(tag, StringComparison.OrdinalIgnoreCase) && Installed < ParseOrZero(tag);
        }
        catch
        {
            return false;
        }
    }

    private static Version ParseOrZero(string tag) => TryParse(tag, out var v) ? v : new Version(0, 0);

    /// <summary>Removes earlier attempt folders that aren't in use any more (best effort).</summary>
    private static void CleanOldAttempts(string keep)
    {
        try
        {
            foreach (var old in Directory.GetDirectories(UpdateRoot))
            {
                if (old.Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(old) < TimeSpan.FromMinutes(10)) continue;
                try { Directory.Delete(old, recursive: true); } catch { }
            }
        }
        catch { }
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
                var attempt = Path.GetDirectoryName(log);
                if (!string.IsNullOrEmpty(attempt)) TryDelete(attempt);
                return $"Updated to FSR 4.1 Lite {Display(target)}.";
            }

            var text = ReadShared(log); // the installer may still have it open
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
}
