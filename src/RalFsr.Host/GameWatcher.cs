using System.Diagnostics;
using RalFsrHelper.Core;

namespace RalFsrHelper.Host;

/// <summary>
/// Watches for running games that have FSR 4.1 Lite installed and sends one notification per launch: when Lite is
/// confirmed active, or when it isn't running (FSR 3 fallback, the driver's own file loaded). Checks every 5
/// seconds, using only the process list and each game's loaded DLLs and OptiScaler.log.
/// </summary>
public sealed class GameWatcher
{
    private sealed class Tracked
    {
        public required string Dir { get; init; }
        public required string Game { get; init; }
        public LoadedModules.Stage Stage { get; set; } = LoadedModules.Stage.None;
        public DateTime StageSince { get; set; } = DateTime.UtcNow;
        public bool Notified { get; set; }
        public bool LoaderRemembered { get; set; }
    }

    private static readonly string[] GraphicsDlls = { "dxgi.dll", "d3d11.dll", "d3d12.dll", "vulkan-1.dll" };

    private const string ManualFix = "Open the widget, press Try another loader and restart the game.";

    /// <summary>
    /// The game ignored OptiScaler's loader name. When that's certain (this is the game's own exe, it has started its
    /// graphics, and no other process from its folder loaded OptiScaler), rename the loader to the next likely name so
    /// the next launch works, with no button to press. Otherwise, or with no name left, just tell the player.
    /// </summary>
    private static void SwitchLoaderOrTell(int pid, string exePath, string dir, InstallManifest manifest, string game)
    {
        var exe = Path.GetFileName(exePath);
        if (manifest.ExeName.Length > 0 && !manifest.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase))
            return; // a launcher or helper from the game's folder, not the game itself
        if (ProxyLoadedElsewhere(dir, manifest.ProxyName))
            return; // another process from the folder runs OptiScaler: this one is a helper

        var rendering = LoadedModules.List(pid).Any(m => GraphicsDlls.Contains(Path.GetFileName(m), StringComparer.OrdinalIgnoreCase));
        if (!rendering)
        {
            Log.Info($"{exe} didn't load {manifest.ProxyName} and has no graphics loaded yet; not switching");
            Toasts.Show($"FSR 4.1 Lite didn't load in {game}", ManualFix);
            return;
        }

        string? next;
        try { next = Installer.SwitchProxy(dir, automatic: true); }
        catch (Exception ex)
        {
            Log.Warn($"Automatic loader switch failed in {dir}: {ex.Message}");
            Toasts.Show($"FSR 4.1 Lite didn't load in {game}", ManualFix);
            return;
        }

        if (next is not null)
            Toasts.Show($"Restart {game} to finish setting up",
                "The game skipped FSR 4.1 Lite, so the widget set it up a different way. Restart the game to use it.");
        else
            Toasts.Show($"FSR 4.1 Lite can't load in {game}",
                "The game skipped every way OptiScaler can start. Uninstall it from this game in the widget.");
    }

    private static bool ProxyLoadedElsewhere(string dir, string proxy)
    {
        var prefix = dir.TrimEnd('\\') + "\\";
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var path = ForegroundGame.ProcessPath(p.Id);
                if (path is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // The Windows copy of the same name (System32\dxgi.dll) doesn't count, only OptiScaler's.
                if (LoadedModules.List(p.Id).Any(m => Path.GetFileName(m).Equals(proxy, StringComparison.OrdinalIgnoreCase) &&
                                                      !m.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>Active but not yet confirmed by the log: notify anyway after this long.</summary>
    private static readonly TimeSpan ActiveGrace = TimeSpan.FromSeconds(20);

    /// <summary>A problem state has to persist this long before it's reported (the game may still be starting).</summary>
    private static readonly TimeSpan ProblemGrace = TimeSpan.FromSeconds(30);

    private readonly Dictionary<int, Tracked> _tracked = new();

    public DateTime LastGameSeen { get; private set; } = DateTime.MinValue;

    /// <summary>Runs until <paramref name="keepRunning"/> returns false.</summary>
    public async Task RunAsync(Func<GameWatcher, bool> keepRunning)
    {
        Log.Info("Game watcher started");
        while (keepRunning(this))
        {
            // Scans run on the thread pool, never on the message-loop thread, so the loop always answers Windows.
            try { await Task.Run(Tick).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"Watcher tick failed: {ex.Message}"); }
            await Task.Delay(Interval).ConfigureAwait(false);
        }
        Log.Info("Game watcher stopped");
    }

    private void Tick()
    {
        var dirs = InstallRegistry.Load();
        if (dirs.Count == 0) { _tracked.Clear(); return; }

        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var path = ForegroundGame.ProcessPath(p.Id);
                if (path is null) continue;
                var dir = dirs.FirstOrDefault(d =>
                    path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                if (dir is null) continue;

                alive.Add(p.Id);
                LastGameSeen = DateTime.UtcNow;
                var manifest = InstallManifest.TryLoad(dir);
                if (manifest is null) continue;

                if (!_tracked.TryGetValue(p.Id, out var t))
                    _tracked[p.Id] = t = new Tracked { Dir = dir, Game = manifest.GameName };
                if (t.Notified) continue;

                var status = LoadedModules.Check(p.Id, dir, manifest);
                if (!t.LoaderRemembered && manifest.TriedLoaders.Count > 0 && status.Stage is LoadedModules.Stage.WaitingForFsr
                        or LoadedModules.Stage.Active or LoadedModules.Stage.NotLite or LoadedModules.Stage.Fsr3Fallback)
                {
                    // A switched loader just worked: start with it if the game is ever installed again.
                    LoaderMemory.Remember(manifest.ExeName.Length > 0 ? manifest.ExeName : Path.GetFileName(path), manifest.ProxyName);
                    t.LoaderRemembered = true;
                }
                if (status.Stage != t.Stage)
                {
                    t.Stage = status.Stage;
                    t.StageSince = DateTime.UtcNow;
                }
                var held = DateTime.UtcNow - t.StageSince;

                switch (status.Stage)
                {
                    case LoadedModules.Stage.Active when status.Confirmed || held >= ActiveGrace:
                        Toasts.Show("FSR 4.1 Lite active",
                            $"{t.Game} · {status.Api}",
                            status.HasResolution ? ResolutionLine(status) : "");
                        t.Notified = true;
                        break;

                    case LoadedModules.Stage.NotLite when held >= ProblemGrace:
                        Toasts.Show($"FSR 4.1 Lite isn't running in {t.Game}",
                            status.Detail.Contains("DriverStore", StringComparison.OrdinalIgnoreCase)
                                ? "FSR 4 loaded from the AMD driver, which has no Lite model. Restart the game."
                                : "FSR 4 loaded from another copy, without the Lite model. Open the widget for details.");
                        t.Notified = true;
                        break;

                    case LoadedModules.Stage.NotLoaded when held >= ProblemGrace:
                        SwitchLoaderOrTell(p.Id, path, dir, manifest, t.Game);
                        t.Notified = true;
                        break;

                    case LoadedModules.Stage.Fsr3Fallback when held >= ProblemGrace:
                        Toasts.Show($"FSR 4.1 Lite isn't running in {t.Game}",
                            "The game is using FSR 3. Open the widget, uninstall from this game, install again and restart it.");
                        t.Notified = true;
                        break;
                }
            }
        }

        foreach (var gone in _tracked.Keys.Where(id => !alive.Contains(id)).ToList()) _tracked.Remove(gone);
        FinishPendingUninstalls();
    }

    private static readonly object UninstallGate = new();

    /// <summary>
    /// Uninstalls from every game the player set to "uninstall when the game closes" that no longer has a process
    /// running from its folder, with a notification for each. Returns the folders it restored.
    /// </summary>
    public static List<string> FinishPendingUninstalls()
    {
        var done = new List<string>();
        lock (UninstallGate)
        {
            foreach (var dir in InstallRegistry.Load())
            {
                var manifest = InstallManifest.TryLoad(dir);
                if (manifest is not { RemoveOnExit: true } || ForegroundGame.AnyRunningFrom(dir)) continue;
                try
                {
                    Installer.Uninstall(dir, manifest);
                    InstallRegistry.Remove(dir);
                    done.Add(dir);
                    Log.Info($"Uninstalled from {manifest.GameName} after it closed");
                    Toasts.Show($"Uninstalled from {manifest.GameName}", "Its folder is back to how it was.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Uninstall after close failed in {dir}: {ex.Message}");
                }
            }
        }
        return done;
    }

    private static string ResolutionLine(LoadedModules.Status s) =>
        s.RenderHeight > 0 && s.DisplayHeight > 0
            ? $"Rendering {s.RenderWidth}x{s.RenderHeight} → {s.DisplayWidth}x{s.DisplayHeight}"
            : $"Rendering {s.RenderWidth} → {s.DisplayWidth} pixels wide";
}
