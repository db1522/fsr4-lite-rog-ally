using System.Diagnostics;

namespace RalFsrHelper.Core;

public enum GameState
{
    /// <summary>A supported upscaler was found and nothing blocks an install.</summary>
    Ready,

    /// <summary>No upscaler DLL found. Many engines build FSR in, so an install is allowed with a warning.</summary>
    NoUpscalerFound,

    /// <summary>Kernel or online anti-cheat found. Installs are blocked to avoid bans.</summary>
    AntiCheat,

    /// <summary>The app's own install is present.</summary>
    Installed,

    /// <summary>OptiScaler is already there from a manual install. The app leaves it alone.</summary>
    ManualOptiScaler,

    /// <summary>The app couldn't work out which folder holds the game exe.</summary>
    NoExe,
}

public sealed class GameScan
{
    public required GameEntry Game { get; init; }
    public string? ExeDir { get; init; }
    public string? ExeName { get; init; }
    public bool IsUnreal { get; init; }
    public List<string> Upscalers { get; init; } = new();
    public string? AntiCheat { get; init; }
    public InstallManifest? Manifest { get; init; }
    public GameState State { get; init; }
}

/// <summary>Works out where OptiScaler has to go for a game and whether it is safe to install there.</summary>
public static class GameAnalyzer
{
    private static readonly Dictionary<string, string> UpscalerDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amd_fidelityfx_dx12.dll"] = "FSR 3.1",
        ["amd_fidelityfx_upscaler_dx12.dll"] = "FSR 3.1",
        ["amd_fidelityfx_loader_dx12.dll"] = "FSR 3.1",
        ["amd_fidelityfx_vk.dll"] = "FSR 3.1",
        ["ffx_fsr2_api_x64.dll"] = "FSR 2",
        ["ffx_fsr2_api_dx12_x64.dll"] = "FSR 2",
        ["ffx_fsr2_api_vk_x64.dll"] = "FSR 2",
        ["nvngx_dlss.dll"] = "DLSS",
        ["libxess.dll"] = "XeSS",
        ["libxess_dx11.dll"] = "XeSS",
    };

    /// <summary>Anti-cheat folders shipped inside game installs, mapped to the name shown to the user.</summary>
    private static readonly Dictionary<string, string> AntiCheatDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EasyAntiCheat"] = "Easy Anti-Cheat",
        ["EasyAntiCheat_EOS"] = "Easy Anti-Cheat",
        ["BattlEye"] = "BattlEye",
        ["EAAntiCheat"] = "EA Javelin anti-cheat",
        ["GameGuard"] = "nProtect GameGuard",
        ["XIGNCODE"] = "XIGNCODE3",
        ["AntiCheatExpert"] = "Anti-Cheat Expert (ACE)",
        ["equ8"] = "EQU8",
    };

    /// <summary>
    /// Anti-cheat files, plus the exes of games whose anti-cheat lives outside the game folder
    /// (kernel drivers installed system-wide, or Valve Anti-Cheat, which runs inside Steam).
    /// </summary>
    private static readonly Dictionary<string, string> AntiCheatFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Files shipped in game folders
        ["EasyAntiCheat_x64.dll"] = "Easy Anti-Cheat",
        ["EasyAntiCheat_EOS_Setup.exe"] = "Easy Anti-Cheat",
        ["start_protected_game.exe"] = "Easy Anti-Cheat",
        ["BEClient_x64.dll"] = "BattlEye",
        ["BEService_x64.exe"] = "BattlEye",
        ["BEService.exe"] = "BattlEye",
        ["EAAntiCheat.GameServiceLauncher.exe"] = "EA Javelin anti-cheat",
        ["EAAntiCheat.Installer.exe"] = "EA Javelin anti-cheat",
        ["randgrid.sys"] = "Ricochet anti-cheat",
        ["GameMon.des"] = "nProtect GameGuard",
        ["x3.xem"] = "XIGNCODE3",
        ["ACE-Base.sys"] = "Anti-Cheat Expert (ACE)",
        ["mhyprot2.sys"] = "HoYoverse anti-cheat",
        ["mhyprot3.sys"] = "HoYoverse anti-cheat",
        ["HoYoKProtect.sys"] = "HoYoverse anti-cheat",
        ["pbsvc.exe"] = "PunkBuster",
        ["pbcl.dll"] = "PunkBuster",

        // Riot Vanguard (kernel driver installed system-wide)
        ["VALORANT-Win64-Shipping.exe"] = "Riot Vanguard",
        ["League of Legends.exe"] = "Riot Vanguard",
        // Valve Anti-Cheat (runs inside Steam). CS2 banned players for AMD Anti-Lag+ injecting in 2023.
        ["cs2.exe"] = "Valve Anti-Cheat",
        ["dota2.exe"] = "Valve Anti-Cheat",
        // Call of Duty (Ricochet)
        ["cod.exe"] = "Ricochet anti-cheat",
        ["BlackOps6.exe"] = "Ricochet anti-cheat",
        ["ModernWarfare.exe"] = "Ricochet anti-cheat",
        // Blizzard
        ["Overwatch.exe"] = "Blizzard anti-cheat",
        // HoYoverse
        ["GenshinImpact.exe"] = "HoYoverse anti-cheat",
        ["StarRail.exe"] = "HoYoverse anti-cheat",
        ["ZenlessZoneZero.exe"] = "HoYoverse anti-cheat",
    };

    /// <summary>Executables that are never the game itself.</summary>
    private static readonly string[] NotGameExeParts =
    {
        "crashreport", "crashhandler", "crashpad", "unrealcefsubprocess", "cefprocess", "unitycrashhandler",
        "uninst", "setup", "vc_redist", "vcredist", "dxsetup", "ue4prereq", "ueprereq", "easyanticheat",
        "start_protected_game", "redist", "installer", "dotnet", "battleye", "beservice",
    };

    /// <summary>Folders skipped when hunting for the game exe or DLLs.</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "__redist", "Redist", "redistributables", "DirectX", "Support", "Prerequisites",
        InstallConfig.MetaFolder, InstallConfig.OptiSubfolder,
    };

    public static GameScan Analyze(GameEntry game)
    {
        var (exeDir, exeName, isUnreal) = game.ExePath is not null && File.Exists(game.ExePath)
            ? (Path.GetDirectoryName(game.ExePath), Path.GetFileName(game.ExePath),
               game.ExePath.Contains(Path.Combine("Binaries", "Win64"), StringComparison.OrdinalIgnoreCase))
            : FindExeDir(game.InstallRoot);
        if (exeDir is null)
        {
            return new GameScan { Game = game, State = GameState.NoExe };
        }

        var manifest = InstallManifest.TryLoad(exeDir);
        var upscalers = FindUpscalers(game.InstallRoot);
        var antiCheat = (exeName is not null && AntiCheatFiles.TryGetValue(exeName, out var byExe) ? byExe : null)
                        ?? FindAntiCheat(game.InstallRoot);

        GameState state;
        if (manifest is not null) state = GameState.Installed;
        else if (HasManualOptiScaler(exeDir)) state = GameState.ManualOptiScaler;
        else if (antiCheat is not null) state = GameState.AntiCheat;
        else if (upscalers.Count == 0) state = GameState.NoUpscalerFound;
        else state = GameState.Ready;

        return new GameScan
        {
            Game = game,
            ExeDir = exeDir,
            ExeName = exeName,
            IsUnreal = isUnreal,
            Upscalers = upscalers,
            AntiCheat = antiCheat,
            Manifest = manifest,
            State = state,
        };
    }

    /// <summary>
    /// Unreal Engine games: the root exe is only a launcher, so OptiScaler goes next to
    /// &lt;Project&gt;\Binaries\Win64\*-Win64-Shipping.exe (never Engine\Binaries).
    /// Everything else: the folder holding the largest real game exe.
    /// </summary>
    public static (string? Dir, string? Exe, bool IsUnreal) FindExeDir(string root)
    {
        // 1. Unreal shipping exe anywhere in the expected place.
        foreach (var project in SafeDirs(root))
        {
            if (Path.GetFileName(project).Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;
            var win64 = Path.Combine(project, "Binaries", "Win64");
            if (!Directory.Exists(win64)) continue;

            var shipping = SafeFiles(win64, "*-Win64-Shipping.exe").FirstOrDefault();
            if (shipping is not null) return (win64, Path.GetFileName(shipping), true);
        }

        // 2. Unreal layout without the -Shipping suffix.
        if (Directory.Exists(Path.Combine(root, "Engine")))
        {
            foreach (var project in SafeDirs(root))
            {
                if (Path.GetFileName(project).Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;
                var win64 = Path.Combine(project, "Binaries", "Win64");
                var exe = SafeFiles(win64, "*.exe").Where(IsGameExe).OrderByDescending(FileSize).FirstOrDefault();
                if (exe is not null) return (win64, Path.GetFileName(exe), true);
            }
        }

        // 3. Any other engine: largest plausible exe within three levels.
        var best = FilesUpTo(root, 3)
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsGameExe(f))
            .OrderByDescending(FileSize)
            .FirstOrDefault();

        return best is null ? (null, null, false) : (Path.GetDirectoryName(best), Path.GetFileName(best), false);
    }

    private static List<string> FindUpscalers(string root) =>
        FilesUpTo(root, 5)
            .Select(Path.GetFileName)
            .Where(n => n is not null && UpscalerDlls.ContainsKey(n))
            .Select(n => UpscalerDlls[n!])
            .Distinct()
            .OrderBy(n => n)
            .ToList();

    private static string? FindAntiCheat(string root)
    {
        foreach (var dir in DirsUpTo(root, 3))
        {
            if (AntiCheatDirs.TryGetValue(Path.GetFileName(dir), out var name)) return name;
        }

        foreach (var file in FilesUpTo(root, 3))
        {
            if (AntiCheatFiles.TryGetValue(Path.GetFileName(file), out var name)) return name;
        }
        return null;
    }

    /// <summary>True when a proxy DLL in the folder is really OptiScaler but wasn't put there by this app.</summary>
    public static bool HasManualOptiScaler(string exeDir)
    {
        foreach (var proxy in InstallConfig.ProxyNames.Append("OptiScaler.dll").Append("OptiScaler.asi"))
        {
            var path = Path.Combine(exeDir, proxy);
            if (!File.Exists(path)) continue;
            if (IsOptiScalerBinary(path)) return true;
        }
        return File.Exists(Path.Combine(exeDir, "OptiScaler.ini"));
    }

    public static bool IsOptiScalerBinary(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.OriginalFilename, "OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                   (info.ProductName?.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGameExe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return !NotGameExeParts.Any(name.Contains);
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    internal static IEnumerable<string> FilesUpTo(string root, int maxDepth)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            foreach (var f in SafeFiles(dir, "*")) yield return f;
            if (depth >= maxDepth) continue;
            foreach (var sub in SafeDirs(dir))
            {
                if (!SkipDirs.Contains(Path.GetFileName(sub))) stack.Push((sub, depth + 1));
            }
        }
    }

    private static IEnumerable<string> DirsUpTo(string root, int maxDepth)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (depth >= maxDepth) continue;
            foreach (var sub in SafeDirs(dir))
            {
                yield return sub;
                stack.Push((sub, depth + 1));
            }
        }
    }

    private static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}
