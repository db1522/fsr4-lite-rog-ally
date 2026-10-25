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
            try { Tick(); }
            catch (Exception ex) { Log.Warn($"Watcher tick failed: {ex.Message}"); }
            await Task.Delay(Interval);
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

                    case LoadedModules.Stage.Fsr3Fallback when held >= ProblemGrace:
                        Toasts.Show($"FSR 4.1 Lite isn't running in {t.Game}",
                            "The game is using FSR 3. Open the widget, uninstall from this game, install again and restart it.");
                        t.Notified = true;
                        break;
                }
            }
        }

        foreach (var gone in _tracked.Keys.Where(id => !alive.Contains(id)).ToList()) _tracked.Remove(gone);
    }

    private static string ResolutionLine(LoadedModules.Status s) =>
        s.RenderHeight > 0 && s.DisplayHeight > 0
            ? $"Rendering {s.RenderWidth}x{s.RenderHeight} → {s.DisplayWidth}x{s.DisplayHeight}"
            : $"Rendering {s.RenderWidth} → {s.DisplayWidth} pixels wide";
}
