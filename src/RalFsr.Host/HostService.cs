using System.Diagnostics;
using System.Text.Json;
using RalFsrHelper.Core;

namespace RalFsrHelper.Host;

/// <summary>
/// Everything the widget asks for. Each command returns one JSON string. Long work (the OptiScaler download)
/// runs in the background and is reported through "state", so no request blocks the widget.
/// </summary>
public sealed class HostService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly DeviceInfo _device = DeviceCheck.Detect();
    private readonly AppSettings _settings = AppSettings.Load();
    private OptiPackage? _opti = OptiScalerSource.Cached();
    private Task? _optiTask;
    private string _optiStatus = "";
    private Task? _fsr4Task;
    private string _fsr4Status = "";
    private DateTime _fsr4StartedAt;

    private List<GameEntry>? _catalog;
    private DateTime _catalogAt;

    /// <summary>The last game found under the overlay, keyed by its exe folder, so install acts on exactly what was shown.</summary>
    private readonly Dictionary<string, GameScan> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folder scans are cached: the widget polls every couple of seconds for the live badge, and walking a large game
    /// folder that often would cost battery. Install and remove clear the entry.
    /// </summary>
    private readonly Dictionary<string, (GameScan Scan, DateTime At)> _scanCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ScanCacheTime = TimeSpan.FromMinutes(2);

    public Task<string> HandleAsync(string cmd, IReadOnlyDictionary<string, string> args)
    {
        try
        {
            object result = cmd switch
            {
                "state" => State(Arg(args, "targetName"), Arg(args, "targetIsGame") == "true"),
                "prepare" => Prepare(),
                "updateFsr4" => StartFsr4Download(force: true),
                "install" => Install(Arg(args, "exeDir") ?? ""),
                "remove" => Remove(Arg(args, "exeDir") ?? ""),
                "removeAll" => RemoveAll(),
                "import" => Import(Arg(args, "paths") ?? "", Arg(args, "allowUnverified") == "true"),
                "overlay" => Overlay(Arg(args, "on") == "true"),
                "watermark" => Watermark(Arg(args, "on") == "true"),
                _ => Fail($"Unknown command: {cmd}"),
            };
            return Task.FromResult(JsonSerializer.Serialize(result, Json));
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error($"{cmd} denied", ex);
            return Task.FromResult(JsonSerializer.Serialize(Fail(
                "Windows blocked access to the game folder. Check that the folder isn't read-only, " +
                "or move the game to a library folder you own."), Json));
        }
        catch (Exception ex)
        {
            Log.Error($"{cmd} failed", ex);
            return Task.FromResult(JsonSerializer.Serialize(Fail(ex.Message), Json));
        }
    }

    public static string ErrorJson(string message) => JsonSerializer.Serialize(Fail(message), Json);

    // ---------------------------------------------------------------- state

    private object State(string? targetName, bool targetIsGame)
    {
        if (_opti is null && _optiTask is null) Prepare();
        // Downloads the FSR 4 files if missing, otherwise a daily update check. A failed attempt retries every 30 s.
        if (_fsr4Task is null ||
            (_fsr4Task.IsCompleted && !Fsr4Library.Load().IsUsable && DateTime.Now - _fsr4StartedAt > TimeSpan.FromSeconds(30)))
        {
            StartFsr4Download(force: false);
        }

        var fsr4 = Fsr4Library.Load();
        var game = CurrentGame(targetName, targetIsGame);

        var installed = InstallRegistry.Load().Select(dir =>
        {
            var m = InstallManifest.TryLoad(dir);
            return new { name = m?.GameName ?? Path.GetFileName(dir), exeDir = dir, running = ForegroundGame.AnyRunningFrom(dir) };
        }).OrderBy(i => i.name, StringComparer.OrdinalIgnoreCase).ToList();

        return new
        {
            ok = true,
            device = new
            {
                summary = _device.Summary,
                supported = _device.Support == DeviceSupport.Supported,
                officialDriver = _device.Support == DeviceSupport.OfficialDriver,
                message = _device.Message,
            },
            opti = new
            {
                ready = _opti is not null,
                build = _opti?.Build ?? "",
                status = _opti is not null && _optiTask is null ? $"OptiScaler {ShortBuild(_opti.Build)}" : _optiStatus,
                busy = _optiTask is not null,
            },
            fsr4 = new
            {
                busy = _fsr4Task is not null && !_fsr4Task.IsCompleted,
                status = _fsr4Status,
                ready = fsr4.IsUsable,
                version = fsr4.VersionLabel,
                verified = fsr4.Dx12?.Signature == SignatureCheck.AmdVerified,
                hasVulkan = fsr4.Vulkan is not null,
            },
            overlay = _settings.ShowResolutionOverlay,
            watermark = _settings.ShowFsr4Watermark,
            game,
            installed,
        };
    }

    private object? CurrentGame(string? targetName, bool targetIsGame)
    {
        var window = ForegroundGame.Find();
        if (window is null) return null;

        var exe = window.ExePath;
        var exeDir = Path.GetDirectoryName(exe)!;

        var scan = CachedScan(exeDir) ?? ScanGame(exe, exeDir, targetName, targetIsGame);
        if (scan is null) return null; // not a game

        lock (_seen) _seen[exeDir] = scan;

        var live = scan.Manifest is not null
            ? LoadedModules.Check(window.ProcessId, exeDir, scan.Manifest)
            : new LoadedModules.Status(LoadedModules.Stage.None, "");

        return new
        {
            name = scan.Game.Name,
            source = scan.Game.SourceLabel,
            exeDir,
            exeName = Path.GetFileName(exe),
            state = scan.State.ToString(),
            stateText = StateText(scan),
            canInstall = scan.State is GameState.Ready or GameState.NoUpscalerFound,
            installed = scan.State == GameState.Installed,
            upscalers = scan.Upscalers,
            activeStage = live.Stage.ToString(),
            activeText = ActiveText(live, scan.Game.Name),
        };
    }

    private GameScan? CachedScan(string exeDir)
    {
        lock (_scanCache)
        {
            return _scanCache.TryGetValue(exeDir, out var hit) && DateTime.Now - hit.At < ScanCacheTime ? hit.Scan : null;
        }
    }

    /// <summary>Full folder scan of the running game. Null when the program doesn't look like a game.</summary>
    private GameScan? ScanGame(string exe, string exeDir, string? targetName, bool targetIsGame)
    {
        var match = MatchCatalog(exe);
        var root = match?.InstallRoot ?? GuessRoot(exeDir);

        // Only treat it as a game if a launcher knows it, Game Bar says so, or the folder looks like one.
        if (match is null && !targetIsGame && !LooksLikeGame(root)) return null;

        var name = match?.Name
                   ?? (targetIsGame && !string.IsNullOrWhiteSpace(targetName) ? targetName : null)
                   ?? ProductName(exe)
                   ?? Path.GetFileNameWithoutExtension(exe);

        var entry = new GameEntry(match?.Source ?? GameSource.Manual, match?.Key ?? $"running:{exe.ToLowerInvariant()}", name, root)
        {
            SteamAppId = match?.SteamAppId,
            ExePath = exe,
        };

        var scan = GameAnalyzer.Analyze(entry);
        lock (_scanCache) _scanCache[exeDir] = (scan, DateTime.Now);
        return scan;
    }

    private static string ActiveText(LoadedModules.Status live, string game) => live.Stage switch
    {
        LoadedModules.Stage.Active => $"FSR 4 Lite active ({live.Api})",
        LoadedModules.Stage.WaitingForFsr => "OptiScaler running. Turn on FSR in the game's graphics settings.",
        LoadedModules.Stage.NeedsRestart => $"Installed. Restart {game} to load it.",
        _ => "",
    };

    private static string StateText(GameScan s)
    {
        var xboxNote = s.Game.Source == GameSource.Xbox ? " Xbox app games are experimental: some block OptiScaler." : "";
        return s.State switch
        {
            GameState.Ready => $"{string.Join(", ", s.Upscalers)} found. Ready to install.{xboxNote}",
            GameState.NoUpscalerFound =>
                "No upscaler files found. Some engines build FSR in, so it may still work: install, restart the game " +
                $"and turn on FSR in its settings.{xboxNote}",
            GameState.AntiCheat => $"{s.AntiCheat} found. Injecting OptiScaler could get your account banned, so installs are blocked.",
            GameState.Installed => "FSR 4 Lite is installed. Turn on FSR in the game's settings.",
            GameState.ManualOptiScaler => "OptiScaler is already here from a manual install. Remove it by hand to manage this game here.",
            _ => "Couldn't find this game's files.",
        };
    }

    // ---------------------------------------------------------------- commands

    private object Prepare()
    {
        if (_optiTask is not null) return new { ok = true };
        var progress = new Progress<string>(s => _optiStatus = s);
        _optiStatus = "Getting OptiScaler...";
        _optiTask = Task.Run(async () =>
        {
            try
            {
                var pkg = await OptiScalerSource.EnsureLatestAsync(progress);
                if (pkg is not null) _opti = pkg;
                else if (_opti is null) _optiStatus = "Couldn't download OptiScaler. Check the connection and press Refresh.";
            }
            finally
            {
                _optiTask = null;
            }
        });
        return new { ok = true };
    }

    /// <summary>
    /// Runs the FSR 4 download in the background and reports progress through "state". After new files land,
    /// every installed game gets them too.
    /// </summary>
    private object StartFsr4Download(bool force)
    {
        if (_fsr4Task is { IsCompleted: false }) return new { ok = true, message = "Already downloading." };

        var progress = new Progress<string>(s => _fsr4Status = s);
        _fsr4StartedAt = DateTime.Now;
        _fsr4Task = Task.Run(async () =>
        {
            var result = await Fsr4Download.EnsureAsync(progress, force);
            if (result.Changed)
            {
                var set = Fsr4Library.Load();
                foreach (var dir in InstallRegistry.Load()) Installer.RefreshFsr4(dir, set);
            }
            _fsr4Status = result.Problem ?? "";
        });
        return new { ok = true, message = force ? "Checking for new FSR 4 files..." : "" };
    }

    private object Install(string exeDir)
    {
        if (_device.Support != DeviceSupport.Supported) return Fail(_device.Message);
        if (_opti is null) return Fail("OptiScaler is still downloading. Try again in a moment.");
        var fsr4 = Fsr4Library.Load();
        if (!fsr4.IsUsable) return Fail("Import the FSR 4.1.2 files first.");

        GameScan? scan;
        lock (_seen) _seen.TryGetValue(exeDir, out scan);
        if (scan is null) return Fail("Open the widget over the game again, then install.");
        if (scan.State is not (GameState.Ready or GameState.NoUpscalerFound)) return Fail(StateText(scan));

        Installer.Install(scan, _opti, fsr4, _settings);
        InstallRegistry.Add(exeDir);
        Forget(exeDir);
        return new
        {
            ok = true,
            message = $"Installed. Restart {scan.Game.Name}, then turn on FSR in its graphics settings. " +
                      "FSR 4 Lite loads automatically, no OptiScaler menu needed.",
        };
    }

    private object Remove(string exeDir)
    {
        var manifest = InstallManifest.TryLoad(exeDir);
        if (manifest is null)
        {
            InstallRegistry.Remove(exeDir);
            return new { ok = true, message = "Already uninstalled." };
        }
        if (ForegroundGame.AnyRunningFrom(exeDir))
        {
            return Fail($"Close {manifest.GameName} first. Windows locks OptiScaler's files while the game is running, so it can't be uninstalled yet.");
        }

        Installer.Uninstall(exeDir, manifest);
        InstallRegistry.Remove(exeDir);
        Forget(exeDir);
        return new { ok = true, message = $"Uninstalled from {manifest.GameName}. Its folder is back to how it was." };
    }

    /// <summary>
    /// First half of "Uninstall FSR 4 Lite": restores every game folder the widget touched.
    /// Running games are skipped, since their files are locked. The widget then opens Windows' uninstall page.
    /// </summary>
    private object RemoveAll()
    {
        var removed = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        foreach (var dir in InstallRegistry.Load())
        {
            var manifest = InstallManifest.TryLoad(dir);
            var name = manifest?.GameName ?? Path.GetFileName(dir);
            if (manifest is null)
            {
                InstallRegistry.Remove(dir);
                continue;
            }
            if (ForegroundGame.AnyRunningFrom(dir))
            {
                skipped.Add(name);
                continue;
            }
            try
            {
                Installer.Uninstall(dir, manifest);
                InstallRegistry.Remove(dir);
                Forget(dir);
                removed.Add(name);
            }
            catch (Exception ex)
            {
                Log.Error($"Remove failed for {name}", ex);
                failed.Add(name);
            }
        }

        var leftOver = skipped.Concat(failed).ToList();
        string message;
        if (leftOver.Count > 0)
        {
            message = (skipped.Count > 0 ? $"Close {string.Join(", ", skipped)} first. " : "") +
                      (failed.Count > 0 ? $"Couldn't restore {string.Join(", ", failed)}. " : "") +
                      "Then press Uninstall FSR 4 Lite again.";
        }
        else
        {
            message = removed.Count == 0
                ? "No games to clean up."
                : $"Removed from {removed.Count} game{(removed.Count == 1 ? "" : "s")}. Their folders are back to how they were.";
        }

        return new { ok = leftOver.Count == 0, removed = removed.Count, readyToUninstall = leftOver.Count == 0, message };
    }

    private object Import(string pathList, bool allowUnverified)
    {
        var paths = pathList.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var temp = Path.Combine(Path.GetTempPath(), "RalFsr4Import");
        try
        {
            var found = Fsr4Library.FindCandidates(paths, temp);
            if (found.Count == 0)
            {
                return Fail("No FSR 4 files in that pick. Choose Official_RDNA2_and_APUs_amdxcffx64.7z, " +
                            "or the amdxcffx64.dll inside it.");
            }

            var problems = new List<string>();
            foreach (var c in found)
            {
                if (c.Signature != SignatureCheck.AmdVerified)
                    problems.Add($"{c.FileName} isn't a signed AMD driver file, or it was changed after signing.");
                if (Fsr4Library.IsOlderThanMinimum(c))
                    problems.Add($"{c.FileName} is version {c.Version}, older than FSR 4.1.2 (2.4.0.4370), so it may not include Lite.");
            }

            if (problems.Count > 0 && !allowUnverified)
            {
                return new { ok = false, needsConfirm = true, message = string.Join(" ", problems) + " Import anyway?" };
            }

            Fsr4Library.Store(found);
            var set = Fsr4Library.Load();

            var skipped = InstallRegistry.Load().Where(dir => !Installer.RefreshFsr4(dir, set)).ToList();
            var note = skipped.Count == 0 ? "" : $" {skipped.Count} running game(s) will pick them up after you close and reopen the widget.";
            return new { ok = true, message = $"FSR 4 files imported ({set.VersionLabel}).{note}" };
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { /* temp cleanup only */ }
        }
    }

    private object Overlay(bool on)
    {
        _settings.ShowResolutionOverlay = on;
        _settings.Save();
        foreach (var dir in InstallRegistry.Load()) Installer.ApplyOverlay(dir, on);
        return new
        {
            ok = true,
            message = on
                ? "Overlay on. Restart a game to see FPS and its render resolution."
                : "Overlay off from the next game launch.",
        };
    }

    private object Watermark(bool on)
    {
        _settings.ShowFsr4Watermark = on;
        _settings.Save();
        foreach (var dir in InstallRegistry.Load()) Installer.ApplyWatermark(dir, on);
        return new
        {
            ok = true,
            message = on
                ? "Watermark on. Restart a game to see the FSR 4 watermark while it runs."
                : "Watermark off from the next game launch.",
        };
    }

    // ---------------------------------------------------------------- helpers

    private void Forget(string exeDir)
    {
        lock (_scanCache) _scanCache.Remove(exeDir);
    }

    private GameEntry? MatchCatalog(string exePath)
    {
        if (_catalog is null || DateTime.Now - _catalogAt > TimeSpan.FromMinutes(2))
        {
            _catalog = GameCatalog.GetAll();
            _catalogAt = DateTime.Now;
        }

        return _catalog
            .Where(g => exePath.StartsWith(g.InstallRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.InstallRoot.Length)
            .FirstOrDefault();
    }

    /// <summary>For Unreal games the exe sits in &lt;root&gt;\&lt;Project&gt;\Binaries\Win64, so the game root is three levels up.</summary>
    private static string GuessRoot(string exeDir)
    {
        var dir = new DirectoryInfo(exeDir);
        if (dir.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase) &&
            dir.Parent?.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) == true &&
            dir.Parent.Parent?.Parent is { } root)
        {
            return root.FullName;
        }
        return exeDir;
    }

    private static readonly string[] GameMarkers =
    {
        "steam_api64.dll", "UnityPlayer.dll", "GameAssembly.dll", "EOSSDK-Win64-Shipping.dll",
        "amd_fidelityfx_dx12.dll", "nvngx_dlss.dll", "libxess.dll", "galaxy64.dll", "uplay_r1_loader64.dll",
    };

    private static bool LooksLikeGame(string root)
    {
        if (Directory.Exists(Path.Combine(root, "Engine"))) return true;
        return GameAnalyzer.FilesUpTo(root, 3)
            .Any(f => GameMarkers.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
    }

    private static string? ProductName(string exe)
    {
        try
        {
            var n = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            return string.IsNullOrEmpty(n) ? null : n;
        }
        catch
        {
            return null;
        }
    }

    private static string ShortBuild(string build) =>
        build.StartsWith("OptiScaler_", StringComparison.OrdinalIgnoreCase) ? build["OptiScaler_".Length..] : build;

    private static string? Arg(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out var v) ? v : null;

    private static object Fail(string message) => new { ok = false, message };
}
