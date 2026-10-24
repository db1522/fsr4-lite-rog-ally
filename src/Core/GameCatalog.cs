namespace RalFsrHelper.Core;

/// <summary>Every installed game the app knows about, from all launchers plus manual additions.</summary>
public static class GameCatalog
{
    public static List<GameEntry> GetAll()
    {
        var games = new List<GameEntry>();
        games.AddRange(Try("Steam", SteamLibrary.GetInstalledGames));
        games.AddRange(OtherLaunchers.GetInstalledGames());
        games.AddRange(Try("Manual", ManualGames.Load));

        // The same folder can show up twice (e.g. a GOG game also added by hand). Keep the first.
        return games
            .GroupBy(g => Normalize(g.ExePath ?? g.InstallRoot))
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();

    private static IEnumerable<GameEntry> Try(string source, Func<List<GameEntry>> read)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log.Warn($"{source} scan failed: {ex.Message}");
            return Array.Empty<GameEntry>();
        }
    }
}
