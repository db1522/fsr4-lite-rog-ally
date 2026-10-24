using System.Text.Json;

namespace RalFsrHelper.Core;

/// <summary>Games the user added by picking their exe. Covers the EA app, Battle.net, itch.io and DRM-free installs.</summary>
public static class ManualGames
{
    private sealed record Saved(string Name, string ExePath);

    private static string FilePath => Path.Combine(AppPaths.Root, "manual-games.json");

    public static List<GameEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(FilePath)) ?? new();
            return saved.Where(s => File.Exists(s.ExePath)).Select(ToEntry).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Manual games list unreadable: {ex.Message}");
            return new();
        }
    }

    public static GameEntry Add(string exePath, string? name = null)
    {
        var saved = ReadSaved();
        saved.RemoveAll(s => s.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase));
        var entry = new Saved(name ?? Path.GetFileNameWithoutExtension(exePath), exePath);
        saved.Add(entry);
        Write(saved);
        return ToEntry(entry);
    }

    public static void Remove(string exePath)
    {
        var saved = ReadSaved();
        saved.RemoveAll(s => s.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase));
        Write(saved);
    }

    /// <summary>
    /// If the user picked an Unreal game's root launcher, ignore that exe and let folder detection
    /// find Binaries\Win64\*-Shipping.exe instead.
    /// </summary>
    private static GameEntry ToEntry(Saved s)
    {
        var dir = Path.GetDirectoryName(s.ExePath)!;
        var pickedUnrealLauncher = Directory.Exists(Path.Combine(dir, "Engine"));
        return new GameEntry(GameSource.Manual, $"manual:{s.ExePath.ToLowerInvariant()}", s.Name, dir)
        {
            ExePath = pickedUnrealLauncher ? null : s.ExePath,
        };
    }

    private static List<Saved> ReadSaved()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    private static void Write(List<Saved> saved)
    {
        AppPaths.Ensure();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
    }
}
