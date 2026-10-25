using System.Text.Json;

namespace RalFsrHelper.Core;

/// <summary>
/// Written to .ralfsr\manifest.json next to the game exe. Lists every file the app added or replaced,
/// so Remove puts the folder back exactly as it was.
/// </summary>
public sealed class InstallManifest
{
    public int FormatVersion { get; set; } = 1;
    public string GameKey { get; set; } = "";
    public string Source { get; set; } = "";
    public string GameName { get; set; } = "";
    public string ProxyName { get; set; } = "";
    public string OptiBuild { get; set; } = "";
    public string Fsr4Version { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.Now;

    /// <summary>Render resolution choice for this game, a key from InstallConfig.RenderScales.</summary>
    public string RenderScale { get; set; } = "game";

    /// <summary>Set once the widget has auto-fixed a game stuck at full resolution, so it never overrides again.</summary>
    public bool AutoFixedNativeAA { get; set; }

    /// <summary>Files the app created, relative to the exe folder.</summary>
    public List<string> CreatedFiles { get; set; } = new();

    /// <summary>Folders the app created, relative to the exe folder. Removed with everything inside them.</summary>
    public List<string> CreatedDirs { get; set; } = new();

    /// <summary>Game files the app replaced. A copy of each original sits in .ralfsr\backup.</summary>
    public List<string> BackedUpFiles { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string MetaDir(string exeDir) => Path.Combine(exeDir, InstallConfig.MetaFolder);
    public static string PathFor(string exeDir) => Path.Combine(MetaDir(exeDir), "manifest.json");
    public static string BackupDir(string exeDir) => Path.Combine(MetaDir(exeDir), "backup");

    public static InstallManifest? TryLoad(string exeDir)
    {
        var path = PathFor(exeDir);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"Manifest unreadable at {path}: {ex.Message}");
            return null;
        }
    }

    public void Save(string exeDir)
    {
        Directory.CreateDirectory(MetaDir(exeDir));
        File.WriteAllText(PathFor(exeDir), JsonSerializer.Serialize(this, Json));
    }
}
