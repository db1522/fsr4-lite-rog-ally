using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RalFsrHelper.Core;

/// <summary>Reads installed games straight from Steam's own library files. Steam doesn't need to be running.</summary>
public static partial class SteamLibrary
{
    private static readonly HashSet<int> NotGames = new() { 228980 }; // Steamworks Common Redistributables

    private static readonly string[] NotGameNameParts =
    {
        "Proton", "Steam Linux Runtime", "Steamworks Common", "Redistributable", "SteamVR",
    };

    public static string? FindSteamRoot()
    {
        var candidates = new[]
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string,
        };

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var path = Path.GetFullPath(c.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(Path.Combine(path, "steamapps"))) return path;
        }
        return null;
    }

    public static List<GameEntry> GetInstalledGames()
    {
        var games = new List<GameEntry>();
        var root = FindSteamRoot();
        if (root is null)
        {
            Log.Warn("Steam install not found in the registry.");
            return games;
        }

        foreach (var library in GetLibraryFolders(root))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var acf in SafeFiles(steamapps, "appmanifest_*.acf"))
            {
                var game = ParseManifest(acf, steamapps);
                if (game is not null) games.Add(game);
            }
        }

        return games
            .GroupBy(g => g.Key)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IEnumerable<string> GetLibraryFolders(string steamRoot)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                foreach (Match m in PathRegex().Matches(File.ReadAllText(vdf)))
                {
                    folders.Add(UnescapeVdf(m.Groups[1].Value));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't read libraryfolders.vdf: {ex.Message}");
            }
        }
        return folders.Where(Directory.Exists);
    }

    private static GameEntry? ParseManifest(string acfPath, string steamapps)
    {
        try
        {
            var text = File.ReadAllText(acfPath);
            var appId = ReadInt(text, "appid");
            var name = ReadString(text, "name");
            var installDir = ReadString(text, "installdir");
            if (appId is null || name is null || installDir is null) return null;
            if (NotGames.Contains(appId.Value)) return null;
            if (NotGameNameParts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase))) return null;

            var root = Path.Combine(steamapps, "common", installDir);
            return Directory.Exists(root)
                ? new GameEntry(GameSource.Steam, $"steam:{appId.Value}", name, root) { SteamAppId = appId.Value }
                : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Skipping {Path.GetFileName(acfPath)}: {ex.Message}");
            return null;
        }
    }

    private static string? ReadString(string text, string key)
    {
        var m = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? UnescapeVdf(m.Groups[1].Value) : null;
    }

    private static int? ReadInt(string text, string key) =>
        int.TryParse(ReadString(text, key), out var v) ? v : null;

    private static string UnescapeVdf(string s) => s.Replace(@"\\", @"\");

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();
}
