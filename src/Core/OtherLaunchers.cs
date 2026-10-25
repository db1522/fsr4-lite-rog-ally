using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RalFsrHelper.Core;

/// <summary>Installed games from launchers other than Steam. Each reader fails quietly if its launcher isn't installed.</summary>
public static class OtherLaunchers
{
    public static List<GameEntry> GetInstalledGames()
    {
        var all = new List<GameEntry>();
        all.AddRange(Safe("Epic", Epic));
        all.AddRange(Safe("GOG", Gog));
        all.AddRange(Safe("Ubisoft Connect", Ubisoft));
        all.AddRange(Safe("Xbox app", Xbox));
        return all;
    }

    /// <summary>Epic writes one JSON .item manifest per installed app.</summary>
    private static IEnumerable<GameEntry> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var file in Directory.GetFiles(dir, "*.item"))
        {
            GameEntry? entry = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                    !cats.EnumerateArray().Any(c => c.GetString()?.Equals("games", StringComparison.OrdinalIgnoreCase) == true))
                    continue; // engines, plugins, tools

                var name = r.GetProperty("DisplayName").GetString();
                var root = r.GetProperty("InstallLocation").GetString();
                var appName = r.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                    entry = new GameEntry(GameSource.Epic, $"epic:{appName ?? name}", name, root);
            }
            catch (Exception ex)
            {
                Log.Warn($"Skipping Epic manifest {Path.GetFileName(file)}: {ex.Message}");
            }
            if (entry is not null) yield return entry;
        }
    }

    /// <summary>GOG Galaxy and the offline installers both register under GOG.com\Games.</summary>
    private static IEnumerable<GameEntry> Gog()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) yield break;

        foreach (var id in games.GetSubKeyNames())
        {
            using var key = games.OpenSubKey(id);
            var name = key?.GetValue("gameName") as string;
            var root = key?.GetValue("path") as string;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                yield return new GameEntry(GameSource.Gog, $"gog:{id}", name, root);
        }
    }

    /// <summary>Ubisoft Connect stores install folders but not names, so the folder name is used.</summary>
    private static IEnumerable<GameEntry> Ubisoft()
    {
        using var installs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (installs is null) yield break;

        foreach (var id in installs.GetSubKeyNames())
        {
            using var key = installs.OpenSubKey(id);
            var root = (key?.GetValue("InstallDir") as string)?.Replace('/', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            yield return new GameEntry(GameSource.Ubisoft, $"ubisoft:{id}", Path.GetFileName(root), root);
        }
    }

    /// <summary>
    /// Xbox app installs into an XboxGames folder on any drive, with the game files under Content.
    /// Confirmed with Valor Mortis. The game reports its files under C:\Program Files\WindowsApps\..., which the
    /// live checks handle by matching file names. A game that ignores dxgi.dll gets "Try another loader".
    /// </summary>
    private static IEnumerable<GameEntry> Xbox()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var xboxGames = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(xboxGames)) continue;

            foreach (var gameDir in Directory.GetDirectories(xboxGames))
            {
                var content = Path.Combine(gameDir, "Content");
                if (!Directory.Exists(content)) continue;
                yield return new GameEntry(GameSource.Xbox, $"xbox:{Path.GetFileName(gameDir)}",
                    ReadXboxName(content) ?? Path.GetFileName(gameDir), content);
            }
        }
    }

    private static string? ReadXboxName(string content)
    {
        var config = Path.Combine(content, "MicrosoftGame.config");
        if (!File.Exists(config)) return null;
        var m = Regex.Match(File.ReadAllText(config), "DefaultDisplayName=\"([^\"]+)\"");
        return m.Success && !m.Groups[1].Value.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)
            ? m.Groups[1].Value : null;
    }

    private static IEnumerable<GameEntry> Safe(string launcher, Func<IEnumerable<GameEntry>> read)
    {
        try
        {
            return read().ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"{launcher} scan failed: {ex.Message}");
            return Array.Empty<GameEntry>();
        }
    }
}
