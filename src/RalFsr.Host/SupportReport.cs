using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using RalFsrHelper.Core;

namespace RalFsrHelper.Host;

/// <summary>
/// "Create support report": one zip on the Desktop with everything needed to diagnose a problem, and nothing private.
///
///   summary.txt          the facts at a glance (device, versions, settings, installed games, the current game)
///   helper.log           the helper's log (last 3,000 lines)
///   widget.log           the widget's own log, if there is one
///   game\...             for the current game: OptiScaler.log (tail), OptiScaler.ini, the install record, and a list of
///                        the file names in the game folder (never the files themselves)
///
/// The Windows user name is replaced with &lt;user&gt; everywhere, and nothing is sent anywhere: the player shares the zip.
/// </summary>
public static class SupportReport
{
    private const int LogLines = 3000;

    public sealed record GameContext(string ExeDir, string Name, string CardText);

    /// <summary>Builds the zip on the Desktop and returns its path.</summary>
    public static string Create(DeviceInfo device, OptiPackage? opti, AppSettings settings, GameContext? game)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var zipPath = Path.Combine(desktop, $"FSR4Lite-report-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
        if (File.Exists(zipPath)) zipPath = Path.Combine(desktop, $"FSR4Lite-report-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        Add(zip, "summary.txt", Summary(device, opti, settings, game));
        Add(zip, "helper.log", Tail(AppPaths.LogFile, LogLines));
        if (WidgetLogPath() is { } widgetLog) Add(zip, "widget.log", Tail(widgetLog, LogLines));

        if (game is not null && Directory.Exists(game.ExeDir))
        {
            var dir = game.ExeDir;
            AddIfExists(zip, "game/OptiScaler.log", Path.Combine(dir, "OptiScaler.log"), tail: true);
            AddIfExists(zip, "game/OptiScaler.ini", Path.Combine(dir, "OptiScaler.ini"), tail: false);
            AddIfExists(zip, "game/install-record.json",
                Path.Combine(dir, InstallConfig.MetaFolder, "manifest.json"), tail: false);
            AddIfExists(zip, "game/dxgi_proxy.status.json", Path.Combine(dir, "dxgi_proxy.status.json"), tail: false);
            Add(zip, "game/files.txt", FileList(dir));
        }

        Log.Info($"Support report created: {Redact(zipPath)}");
        return zipPath;
    }

    // ------------------------------------------------------------------ summary

    private static string Summary(DeviceInfo device, OptiPackage? opti, AppSettings settings, GameContext? game)
    {
        var s = new StringBuilder();
        s.AppendLine("FSR 4.1 Lite support report");
        s.AppendLine($"Created:          {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        s.AppendLine();
        s.AppendLine("== Device ==");
        s.AppendLine($"Device:           {device.Model}");
        s.AppendLine($"CPU:              {device.Cpu}");
        s.AppendLine($"GPU:              {device.Gpu}");
        s.AppendLine($"Support:          {device.Support}");
        s.AppendLine($"Windows:          {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        s.AppendLine();
        s.AppendLine("== Versions ==");
        s.AppendLine($"Widget:           {SelfUpdate.Display(SelfUpdate.Installed)}");
        s.AppendLine($"OptiScaler:       {(opti is null ? "not downloaded" : OptiPackage.Label(opti.Build))}");
        var fsr4 = Fsr4Library.Load();
        s.AppendLine($"FSR 4 DX12 file:  {(fsr4.Dx12 is { } d ? $"{d.Version} ({d.Signature})" : "missing")}");
        s.AppendLine($"FSR 4 Vulkan:     {(fsr4.Vulkan is { } v ? $"{v.Version} ({v.Signature})" : "missing")}");
        s.AppendLine();
        s.AppendLine("== Settings ==");
        s.AppendLine($"Update games automatically:  {settings.AutoUpdateGames}");
        s.AppendLine($"Notify when active:          {settings.NotifyWhenActive}");
        s.AppendLine($"FPS overlay:                 {settings.ShowResolutionOverlay} ({settings.OverlaySize}, {settings.OverlayPosition})");
        s.AppendLine();

        if (game is not null)
        {
            s.AppendLine("== Game on the card ==");
            s.AppendLine($"Name:             {game.Name}");
            s.AppendLine($"Folder:           {Redact(game.ExeDir)}");
            s.AppendLine($"Card said:        {game.CardText}");
            var m = InstallManifest.TryLoad(game.ExeDir);
            s.AppendLine(m is null
                ? "Installed:        no"
                : $"Installed:        yes, as {m.ProxyName}, {OptiPackage.Label(m.OptiBuild)}, FSR 4 {m.Fsr4Version}, " +
                  $"render scale {m.RenderScale}, on {m.InstalledAt:yyyy-MM-dd HH:mm}");
            var optiLog = Path.Combine(game.ExeDir, "OptiScaler.log");
            if (File.Exists(optiLog))
            {
                s.AppendLine($"OptiScaler.log:   last written {File.GetLastWriteTime(optiLog):yyyy-MM-dd HH:mm}");
                var problems = Tail(optiLog, 4000).Split('\n')
                    .Where(l => l.Contains("[error]", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("[critical]", StringComparison.OrdinalIgnoreCase))
                    .TakeLast(10).ToList();
                if (problems.Count > 0)
                {
                    s.AppendLine("OptiScaler errors (last 10):");
                    foreach (var p in problems) s.AppendLine("  " + p.Trim());
                }
            }
            else
            {
                s.AppendLine("OptiScaler.log:   none (OptiScaler hasn't run in this game yet)");
            }
            s.AppendLine();
        }

        s.AppendLine("== Installed in ==");
        var dirs = InstallRegistry.Load();
        if (dirs.Count == 0) s.AppendLine("(no games)");
        foreach (var dir in dirs)
        {
            var m = InstallManifest.TryLoad(dir);
            if (m is null) continue;
            s.AppendLine($"- {m.GameName}: {m.ProxyName}, {OptiPackage.Label(m.OptiBuild)}, {Redact(dir)}");
        }
        s.AppendLine();

        s.AppendLine("== Recent helper problems (last 15) ==");
        var helperProblems = Tail(AppPaths.LogFile, LogLines).Split('\n')
            .Where(l => l.Contains("[ERROR]") || l.Contains("[WARN]")).TakeLast(15).ToList();
        if (helperProblems.Count == 0) s.AppendLine("(none)");
        foreach (var p in helperProblems) s.AppendLine("  " + (p.Length > 300 ? p[..300] + "..." : p.Trim()));

        return Redact(s.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private static string FileList(string dir)
    {
        var s = new StringBuilder();
        s.AppendLine($"Files in {Redact(dir)} (names and sizes only)");
        void List(string folder, string prefix)
        {
            try
            {
                foreach (var f in Directory.GetFiles(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(f);
                    s.AppendLine($"{prefix}{info.Name}  ({info.Length:N0} bytes, {info.LastWriteTime:yyyy-MM-dd HH:mm})");
                }
                foreach (var d in Directory.GetDirectories(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    s.AppendLine($"{prefix}{Path.GetFileName(d)}\\");
            }
            catch (Exception ex)
            {
                s.AppendLine($"{prefix}(couldn't list: {ex.Message})");
            }
        }
        List(dir, "");
        foreach (var sub in new[] { InstallConfig.OptiSubfolder, InstallConfig.MetaFolder })
        {
            var path = Path.Combine(dir, sub);
            if (!Directory.Exists(path)) continue;
            s.AppendLine();
            s.AppendLine($"{sub}\\");
            List(path, "  ");
        }
        return Redact(s.ToString());
    }

    private static string? WidgetLogPath()
    {
        try
        {
            var family = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", family, "LocalState", "widget.log");
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AddIfExists(ZipArchive zip, string name, string path, bool tail)
    {
        if (!File.Exists(path)) return;
        Add(zip, name, tail ? Tail(path, LogLines) : ReadShared(path));
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(Redact(text));
    }

    /// <summary>The last lines of a file, read even while another process has it open.</summary>
    private static string Tail(string path, int lines)
    {
        var text = ReadShared(path);
        if (text.Length == 0) return "";
        var all = text.Split('\n');
        return all.Length <= lines ? text : string.Join('\n', all[^lines..]);
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
        catch (Exception ex)
        {
            return $"(couldn't read: {ex.Message})";
        }
    }

    /// <summary>Replaces the Windows user name (in paths and on its own) with &lt;user&gt;.</summary>
    public static string Redact(string text)
    {
        var user = Environment.UserName;
        if (string.IsNullOrEmpty(user)) return text;
        text = Regex.Replace(text, @"([\\/]Users[\\/])" + Regex.Escape(user) + @"(?=[\\/]|$)", "$1<user>", RegexOptions.IgnoreCase);
        return user.Length >= 3 ? Regex.Replace(text, @"\b" + Regex.Escape(user) + @"\b", "<user>", RegexOptions.IgnoreCase) : text;
    }
}
