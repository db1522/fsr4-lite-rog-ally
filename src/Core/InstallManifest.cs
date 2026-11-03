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

    /// <summary>The game exe the install was made for (file name). "" in installs from before 1.1.30.</summary>
    public string ExeName { get; set; } = "";

    /// <summary>Loader names this game was seen ignoring, in order. The automatic switch never goes back to one.</summary>
    public List<string> TriedLoaders { get; set; } = new();

    /// <summary>
    /// The player asked to uninstall while the game was running (its files are locked then). The helper uninstalls as
    /// soon as no process from the game's folder is left.
    /// </summary>
    public bool RemoveOnExit { get; set; }

    /// <summary>The game window's height in pixels when last seen (0 = not seen yet). The overlay is sized from it.</summary>
    public int ScreenHeight { get; set; }
    public string OptiBuild { get; set; } = "";
    public string Fsr4Version { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.Now;

    /// <summary>Render resolution choice for this game, a key from InstallConfig.RenderScales.</summary>
    public string RenderScale { get; set; } = "game";

    /// <summary>Set by versions before 1.1.33, which forced Quality on games stuck at full resolution. Cleared (and the
    /// render resolution handed back to the game) the next time the widget opens.</summary>
    public bool AutoFixedNativeAA { get; set; }

    /// <summary>REFramework nightly installed alongside OptiScaler ("" = none). Its dinput8.dll is in CreatedFiles.</summary>
    public string REFrameworkVersion { get; set; } = "";

    /// <summary>REFramework's runtime files and folders that already existed before install, so uninstall leaves them.</summary>
    public List<string> REFrameworkPreexisting { get; set; } = new();

    /// <summary>The Experimental frame generation option for this game (off unless the player turned it on).</summary>
    public FrameGenSettings FrameGen { get; set; } = new();

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
