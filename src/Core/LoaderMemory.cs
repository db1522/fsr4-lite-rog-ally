using System.Text.Json;

namespace RalFsrHelper.Core;

/// <summary>
/// Remembers, per game exe, the loader name that worked after a switch, so installing the game again (after an
/// uninstall) starts with that name instead of failing once more. Kept in its own file because the game watcher and
/// the widget's service both write it.
/// </summary>
public static class LoaderMemory
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppPaths.Root, "loaders.json");

    public static string? Recall(string? exeName)
    {
        if (string.IsNullOrEmpty(exeName)) return null;
        lock (Gate) return Load().TryGetValue(Path.GetFileName(exeName), out var name) ? name : null;
    }

    public static void Remember(string? exeName, string loader)
    {
        if (string.IsNullOrEmpty(exeName)) return;
        lock (Gate)
        {
            var all = Load();
            var key = Path.GetFileName(exeName);
            if (all.TryGetValue(key, out var known) && known.Equals(loader, StringComparison.OrdinalIgnoreCase)) return;
            all[key] = loader;
            try
            {
                AppPaths.Ensure();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
                Log.Info($"Remembered loader {loader} for {key}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't save loader memory: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return new Dictionary<string, string>(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"Loader memory unreadable: {ex.Message}");
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }
}
