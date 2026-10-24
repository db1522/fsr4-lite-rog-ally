using System.Text.Json;
using RalFsrHelper.Core;

namespace RalFsrHelper.Host;

/// <summary>Remembers which game folders the widget installed into, so they can be removed later without the game running.</summary>
public static class InstallRegistry
{
    private static string FilePath => Path.Combine(AppPaths.Root, "installs.json");

    public static List<string> Load()
    {
        try
        {
            var list = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new()
                : new List<string>();
            // Drop entries whose install record is gone (removed by hand, game uninstalled).
            return list.Where(d => InstallManifest.TryLoad(d) is not null)
                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return new();
        }
    }

    public static void Add(string exeDir)
    {
        var list = Load();
        if (!list.Contains(exeDir, StringComparer.OrdinalIgnoreCase)) list.Add(exeDir);
        Save(list);
    }

    public static void Remove(string exeDir)
    {
        var list = Load();
        list.RemoveAll(d => d.Equals(exeDir, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }

    private static void Save(List<string> list)
    {
        AppPaths.Ensure();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
    }
}
