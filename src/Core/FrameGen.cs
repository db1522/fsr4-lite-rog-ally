using System.Text.RegularExpressions;

namespace RalFsrHelper.Core;

/// <summary>A game's frame generation choices, kept in its install record whether frame generation is on or off.</summary>
public sealed class FrameGenSettings
{
    public bool Enabled { get; set; }

    /// <summary>OptiScaler frame generation input picked for the game: dlssg, nukems or optifg.</summary>
    public string Input { get; set; } = "";

    /// <summary>fsrfg (more FPS, the default) or xefg (cleaner image).</summary>
    public string Output { get; set; } = FrameGen.DefaultOutput;

    /// <summary>OptiFG only: HUDFix tracking and its limit (1 to 5), and Immediate Capture.</summary>
    public bool HudFix { get; set; } = true;
    public int HudLimit { get; set; } = FrameGen.DefaultHudLimit;
    public bool Immediate { get; set; }

    /// <summary>Unreal Engine.ini the widget added r.NGX.DLSS.DilateMotionVectors=0 to ("" = none).</summary>
    public string EngineIniPath { get; set; } = "";

    /// <summary>The Engine.ini didn't exist before, so the widget created it.</summary>
    public bool EngineIniCreated { get; set; }

    /// <summary>The widget added the [SystemSettings] section header itself.</summary>
    public bool EngineIniAddedSection { get; set; }

    /// <summary>The top config folder the widget had to create (the game hadn't run yet), removed again when empty.</summary>
    public string EngineIniCreatedDir { get; set; } = "";

    /// <summary>The recommended output and fine-tune values: the game's preset when it has one, else the general defaults.</summary>
    public void ApplyRecommended(FrameGenPreset? preset)
    {
        Output = preset?.Output == FrameGen.XessOutput ? FrameGen.XessOutput : FrameGen.DefaultOutput;
        HudFix = preset?.HudFix ?? true;
        HudLimit = Math.Clamp(preset?.HudLimit ?? FrameGen.DefaultHudLimit, FrameGen.MinHudLimit, FrameGen.MaxHudLimit);
        Immediate = preset?.Immediate ?? false;
    }
}

/// <summary>What a game offers for frame generation, from its own files.</summary>
/// <param name="HasDlssFg">The game ships DLSS frame generation (nvngx_dlssg.dll, Streamline's sl.dlss_g.dll).</param>
/// <param name="HasOwnFsrFg">The game has its own FSR frame generation, which works alongside FSR 4.1 Lite.</param>
/// <param name="Streamline1">One of the old Streamline 1 games, where DLSSG via Streamline doesn't work.</param>
public sealed record FrameGenSupport(bool HasDlssFg, bool HasOwnFsrFg, bool Streamline1)
{
    /// <summary>
    /// OptiScaler's frame generation input for this game, by the priority on its Frame-Generation-Options wiki page:
    /// DLSSG via Streamline for games with DLSS frame generation (Streamline 2+), Nukem's for Streamline 1 games, and OptiFG
    /// (upscaler input) only for games with no frame generation at all.
    /// </summary>
    public string Input => HasDlssFg ? (Streamline1 ? FrameGen.Nukems : FrameGen.Dlssg) : FrameGen.OptiFg;
}

/// <summary>
/// The Experimental frame generation option: which OptiScaler input fits a game, the OptiScaler.ini values for it (key
/// names checked against the v10 nightly's OptiScaler.ini), and the Unreal Engine.ini fix for OptiFG with DLSS inputs.
/// OptiScaler's frame generation settings only apply when the game starts.
/// </summary>
public static class FrameGen
{
    public const string Dlssg = "dlssg", Nukems = "nukems", OptiFg = "optifg";
    public const string FsrOutput = "fsrfg", XessOutput = "xefg", DefaultOutput = FsrOutput;
    public const int DefaultHudLimit = 1, MinHudLimit = 1, MaxHudLimit = 5;

    /// <summary>The Streamline 1 games from OptiScaler's wiki (Frame-Generation-Options). Dying Light 2 moved to SL2.</summary>
    private static readonly string[] Streamline1Games =
    {
        "The Witcher 3", "Returnal", "A Plague Tale Requiem", "Marvel's Midnight Suns", "Loopmancer",
        "The Lord of the Rings Gollum", "WRC Generations", "Destroy All Humans! 2 - Reprobed", "F.I.S.T.: Forged In Shadow Torch",
        "Bright Memory Infinite",
    };

    /// <summary>Game exes with FSR frame generation built into the game (no separate DLL to find). Tested: The Witcher 3.</summary>
    private static readonly HashSet<string> OwnFsrFgExes = new(StringComparer.OrdinalIgnoreCase) { "witcher3.exe" };

    private static readonly HashSet<string> DlssFgFiles = new(StringComparer.OrdinalIgnoreCase) { "nvngx_dlssg.dll", "sl.dlss_g.dll" };

    /// <summary>FSR 3.1+ frame generation (separate DLL) and FSR 3.0 frame interpolation DLLs, shipped by games that have it.</summary>
    private static readonly HashSet<string> FsrFgFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "amd_fidelityfx_framegeneration_dx12.dll", "ffx_frameinterpolation_x64.dll", "ffx_fsr3_x64.dll",
    };

    /// <summary>
    /// Reads the game's frame generation support from its files. Files FSR 4.1 Lite added itself (OptiScaler's package
    /// brings FSR frame generation DLLs) never count, nor anything in its OptiScaler subfolder.
    /// </summary>
    public static FrameGenSupport Detect(string gameName, string? exeName, IEnumerable<string> gameFiles, InstallManifest? manifest)
    {
        var ours = new HashSet<string>(manifest?.CreatedFiles.Select(Path.GetFileName).OfType<string>() ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var names = gameFiles.Select(Path.GetFileName).OfType<string>().Where(n => !ours.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dlss = names.Any(DlssFgFiles.Contains);
        var fsr = names.Any(FsrFgFiles.Contains) || (exeName is not null && OwnFsrFgExes.Contains(Path.GetFileName(exeName)));
        var key = CompatList.BaseKey(gameName);
        var sl1 = Streamline1Games.Any(g => CompatList.BaseKey(g) is var k && k.Length > 0 && (key == k || key.StartsWith(k, StringComparison.Ordinal)));
        return new FrameGenSupport(dlss, fsr, sl1);
    }

    /// <summary>
    /// OptiScaler.ini values for a game's frame generation. Off: everything back to fully off. On:
    ///   [FrameGen] Enabled, FGInput, FGOutput, FGNvngxReplacement
    ///     dlssg   FGInput=dlssg, Enabled=auto (follows the game's own DLSS frame generation switch), FGOutput=fsrfg/xefg
    ///     nukems  FGInput=nvngxfg, FGNvngxReplacement=Nukems, FGOutput=auto (Nukem's is locked to FSR 3 frame generation)
    ///     optifg  FGInput=upscaler, Enabled=true, FGOutput=fsrfg/xefg
    ///   [OptiFG] HUDFix, HUDLimit, HUDFixImmediate (OptiFG only; the other inputs bring their own HUD-less image)
    ///   [Hotfix] DisableOverlays=false: OptiScaler turns overlays off by default with OptiFG, which also blocks Steam
    ///     Input and so breaks controller layouts on handhelds. The Epic overlay, which crashes with frame generation,
    ///     OptiScaler blocks by itself whenever frame generation is selected.
    /// </summary>
    public static IReadOnlyList<InstallConfig.IniValue> IniValues(FrameGenSettings s)
    {
        if (!s.Enabled || s.Input.Length == 0)
            return new[]
            {
                new InstallConfig.IniValue("FrameGen", "Enabled", "false"),
                new InstallConfig.IniValue("FrameGen", "FGInput", "nofg"),
                new InstallConfig.IniValue("FrameGen", "FGOutput", "nofg"),
                new InstallConfig.IniValue("FrameGen", "FGNvngxReplacement", "auto"),
                new InstallConfig.IniValue("OptiFG", "HUDFix", "auto"),
                new InstallConfig.IniValue("OptiFG", "HUDLimit", "auto"),
                new InstallConfig.IniValue("OptiFG", "HUDFixImmediate", "auto"),
                new InstallConfig.IniValue("Hotfix", "DisableOverlays", "auto"),
            };

        var output = s.Output == XessOutput ? XessOutput : FsrOutput;
        var optifg = s.Input == OptiFg;
        var values = new List<InstallConfig.IniValue>
        {
            new("FrameGen", "Enabled", s.Input == Dlssg ? "auto" : "true"),
            new("FrameGen", "FGInput", s.Input switch { Dlssg => "dlssg", Nukems => "nvngxfg", _ => "upscaler" }),
            new("FrameGen", "FGOutput", s.Input == Nukems ? "auto" : output),
            new("FrameGen", "FGNvngxReplacement", s.Input == Nukems ? "Nukems" : "auto"),
            new("OptiFG", "HUDFix", optifg ? (s.HudFix ? "true" : "false") : "auto"),
            new("OptiFG", "HUDLimit", optifg ? Math.Clamp(s.HudLimit, MinHudLimit, MaxHudLimit).ToString() : "auto"),
            new("OptiFG", "HUDFixImmediate", optifg ? (s.Immediate ? "true" : "false") : "auto"),
            new("Hotfix", "DisableOverlays", "false"),
        };
        return values;
    }

    // ------------------------------------------------------------------ Unreal Engine.ini

    private const string DilateKey = "r.NGX.DLSS.DilateMotionVectors";
    private const string DilateLine = DilateKey + "=0";

    /// <summary>
    /// Where an Unreal game keeps Engine.ini: %LOCALAPPDATA%\&lt;Project&gt;\Saved\Config\ then Windows (UE5),
    /// WindowsNoEditor (UE4) or WinGDK (Game Pass). The project is the folder above Binaries. An existing Engine.ini wins,
    /// then an existing config folder; before the game's first run, Windows for Steam/Epic builds and WinGDK for Game Pass.
    /// </summary>
    public static string? EngineIniPath(string exeDir)
    {
        var binaries = new DirectoryInfo(exeDir).Parent;
        if (binaries is null || !binaries.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase)) return null;
        var project = binaries.Parent?.Name;
        if (string.IsNullOrEmpty(project)) return null;
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), project, "Saved", "Config");
        var folders = exeDir.EndsWith("WinGDK", StringComparison.OrdinalIgnoreCase)
            ? new[] { "WinGDK", "Windows", "WindowsNoEditor" }
            : new[] { "Windows", "WindowsNoEditor", "WinGDK" };
        foreach (var f in folders)
            if (File.Exists(Path.Combine(config, f, "Engine.ini"))) return Path.Combine(config, f, "Engine.ini");
        foreach (var f in folders)
            if (Directory.Exists(Path.Combine(config, f))) return Path.Combine(config, f, "Engine.ini");
        return Path.Combine(config, folders[0], "Engine.ini");
    }

    /// <summary>
    /// Adds r.NGX.DLSS.DilateMotionVectors=0 under [SystemSettings] (OptiScaler's Unreal-Engine-Tweaks page: dilated motion
    /// vectors smear with OptiFG on DLSS inputs). The original file is copied to the install record's backup folder first.
    /// </summary>
    public static void ApplyEngineIni(string exeDir, FrameGenSettings s)
    {
        if (s.EngineIniPath.Length > 0) return; // already applied
        var path = EngineIniPath(exeDir);
        if (path is null) return;

        var existed = File.Exists(path);
        if (existed)
        {
            var backup = Path.Combine(InstallManifest.BackupDir(exeDir), "Engine.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(path, backup, overwrite: true);
        }
        var lines = existed ? File.ReadAllLines(path).ToList() : new List<string>();
        var hadSection = lines.Any(l => l.Trim().Equals("[SystemSettings]", StringComparison.OrdinalIgnoreCase));
        if (existed && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);

        // Remember the highest folder that didn't exist yet (below %LOCALAPPDATA%), so undo can tidy it away.
        var created = "";
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(path)!); dir is not null && !dir.Exists; dir = dir.Parent)
            created = dir.FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        s.EngineIniCreatedDir = created;
        IniFile.SetValues(path, new[] { new InstallConfig.IniValue("SystemSettings", DilateKey, "0") });
        s.EngineIniPath = path;
        s.EngineIniCreated = !existed;
        s.EngineIniAddedSection = !hadSection;
        Log.Info($"Added {DilateLine} to {path}{(existed ? "" : " (created)")}");
    }

    /// <summary>Takes the line out again (and the section or file, when the widget added them). Never throws.</summary>
    public static void UndoEngineIni(FrameGenSettings? s)
    {
        if (s is null || s.EngineIniPath.Length == 0) return;
        var path = s.EngineIniPath;
        try
        {
            if (File.Exists(path))
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                var lines = File.ReadAllLines(path).ToList();
                lines.RemoveAll(l => Regex.IsMatch(l.Trim(), "^" + Regex.Escape(DilateKey) + @"\s*=", RegexOptions.IgnoreCase));
                if (s.EngineIniAddedSection)
                {
                    // Drop the [SystemSettings] header when nothing else ended up under it.
                    var i = lines.FindIndex(l => l.Trim().Equals("[SystemSettings]", StringComparison.OrdinalIgnoreCase));
                    if (i >= 0)
                    {
                        var next = i + 1;
                        while (next < lines.Count && lines[next].Trim().Length == 0) next++;
                        if (next >= lines.Count || lines[next].TrimStart().StartsWith('['))
                        {
                            lines.RemoveRange(i, next - i);
                            if (i > 0 && i - 1 < lines.Count && lines[i - 1].Trim().Length == 0) lines.RemoveAt(i - 1);
                        }
                    }
                }
                if (s.EngineIniCreated && lines.All(l => l.Trim().Length == 0)) File.Delete(path);
                else File.WriteAllLines(path, lines);
            }
            Log.Info($"Removed {DilateLine} from {path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't remove {DilateLine} from {path}: {ex.Message}");
        }
        try
        {
            // Folders the widget created only for this file: removed when nothing else is in them.
            if (s.EngineIniCreatedDir.Length > 0 && Directory.Exists(s.EngineIniCreatedDir) &&
                !Directory.EnumerateFiles(s.EngineIniCreatedDir, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(s.EngineIniCreatedDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't remove {s.EngineIniCreatedDir}: {ex.Message}");
        }
        s.EngineIniPath = "";
        s.EngineIniCreated = false;
        s.EngineIniAddedSection = false;
        s.EngineIniCreatedDir = "";
    }

    // ------------------------------------------------------------------ log

    // OptiScaler logs these at info level when frame generation is switched on or off in the game:
    //   FSR frame generation (FSRFG_Dx12)  "D3D12_Configure Enabled: true, result: ..."
    //   XeSS frame generation (XeFG_Dx12)  "SetEnabled: true, result: ..."
    private static readonly Regex FsrFgLine = new(@"D3D12_Configure Enabled: (true|false)", RegexOptions.Compiled);
    private static readonly Regex XeFgLine = new(@"SetEnabled: (true|false)", RegexOptions.Compiled);

    /// <summary>"FSR" or "XeSS" when OptiScaler's log shows frame generation running (the last switch was on), else "".</summary>
    public static string ActiveOutput(string log)
    {
        if (log.Length == 0) return "";
        var fsr = FsrFgLine.Matches(log);
        var xe = XeFgLine.Matches(log);
        var lastFsr = fsr.Count > 0 ? fsr[^1] : null;
        var lastXe = xe.Count > 0 ? xe[^1] : null;
        var last = lastFsr is null ? lastXe : lastXe is null ? lastFsr : (lastFsr.Index > lastXe.Index ? lastFsr : lastXe);
        // Nukem's runs FSR 3 frame generation inside its own DLL and logs no on/off switch, so it's never confirmed here.
        if (last is null || last.Groups[1].Value != "true") return "";
        return last == lastXe ? "XeSS" : "FSR";
    }
}
