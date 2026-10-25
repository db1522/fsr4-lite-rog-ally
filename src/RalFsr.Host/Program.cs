using System.Diagnostics;
using RalFsrHelper.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace RalFsrHelper.Host;

/// <summary>
/// Full-trust helper inside the widget's package. Two ways it starts:
///   --widget   launched by the widget: connects back over an app service and does all file work (detect,
///              download, install, remove). When the widget closes it keeps watching games for notifications.
///   (no args)  started by Windows at sign-in (startup task): only watches games and sends notifications.
/// Only one helper runs at a time; a widget-launched helper replaces a running watcher.
/// </summary>
internal static class Program
{
    public const string ServiceName = "com.rogallylife.fsr4lite";

    /// <summary>The widget polls every couple of seconds while open, so this much silence means it's gone.</summary>
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(15);

    private static readonly TaskCompletionSource Closed = new();
    private static DateTime _lastRequest = DateTime.UtcNow;

    /// <summary>After the widget closes with notifications off, keep watching only while a game runs.</summary>
    private static readonly TimeSpan WatchAfterLastGame = TimeSpan.FromMinutes(2);

    private static async Task Main(string[] args)
    {
        AppPaths.Ensure();
        if (args.Contains("--widget", StringComparer.OrdinalIgnoreCase)) await RunForWidgetAsync();
        else await RunWatcherOnlyAsync();
    }

    /// <summary>Started at sign-in: watch games for notifications, unless they're turned off.</summary>
    private static async Task RunWatcherOnlyAsync()
    {
        try
        {
            Log.Info($"Watcher starting (pid {Environment.ProcessId})");
            if (!AppSettings.Load().NotifyWhenActive)
            {
                Log.Info("Notifications are off, watcher exiting");
                return;
            }
            if (OtherHelpersRunning())
            {
                Log.Info("Another helper is already running, watcher exiting");
                return;
            }
            await new GameWatcher().RunAsync(_ => AppSettings.Load().NotifyWhenActive);
        }
        catch (Exception ex)
        {
            Log.Error("Watcher crashed", ex);
        }
    }

    private static async Task RunForWidgetAsync()
    {
        try
        {
            Log.Info($"Host starting (pid {Environment.ProcessId})");

            // A helper left over from an earlier session or version, or the sign-in watcher, would otherwise block
            // this one. The newest helper always wins: close any other copy instead of exiting.
            StopOtherHelpers();

            // Notifications keep working while the widget is open, and after it closes.
            var widgetOpen = true;
            var watcher = new GameWatcher();
            var watching = watcher.RunAsync(w =>
                widgetOpen || AppSettings.Load().NotifyWhenActive || DateTime.UtcNow - w.LastGameSeen < WatchAfterLastGame);

            // Build the service (device check, settings) in parallel with connecting, so a slow first start
            // can't make the widget give up waiting for the connection.
            var serviceTask = Task.Run(() =>
            {
                var created = new HostService();
                Log.Info("Host service ready");
                return created;
            });

            var connection = new AppServiceConnection
            {
                AppServiceName = ServiceName,
                PackageFamilyName = Package.Current.Id.FamilyName,
            };

            connection.RequestReceived += async (_, e) =>
            {
                _lastRequest = DateTime.UtcNow;
                var deferral = e.GetDeferral();
                try
                {
                    var message = e.Request.Message;
                    var args = message.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
                    var cmd = args.TryGetValue("cmd", out var c) ? c : "";

                    string json;
                    try { json = await (await serviceTask).HandleAsync(cmd, args); }
                    catch (Exception ex) { json = HostService.ErrorJson(ex.Message); }

                    await e.Request.SendResponseAsync(new ValueSet { ["json"] = json });
                }
                catch (Exception ex)
                {
                    Log.Error("Request handling failed", ex);
                }
                finally
                {
                    deferral.Complete();
                }
            };

            connection.ServiceClosed += (_, a) =>
            {
                Log.Info($"Widget connection closed ({a.Status})");
                Closed.TrySetResult();
            };

            Log.Info($"Connecting to the widget ({Package.Current.Id.FamilyName})");
            var status = await connection.OpenAsync();
            if (status != AppServiceConnectionStatus.Success)
            {
                Log.Error($"App service connection failed: {status}");
                widgetOpen = false;
                await watching;
                return;
            }
            Log.Info("Connected to the widget");

            // Exit when the widget closes, or after a long silence in case the close was never reported.
            while (!Closed.Task.IsCompleted)
            {
                await Task.WhenAny(Closed.Task, Task.Delay(TimeSpan.FromMinutes(1)));
                if (DateTime.UtcNow - _lastRequest > IdleExit)
                {
                    Log.Info("No requests for 15 minutes, host exiting");
                    break;
                }
            }

            connection.Dispose();
            widgetOpen = false;
            Log.Info("Widget gone; continuing as the game watcher while it's needed");
            await watching;
            Log.Info("Host exiting");
        }
        catch (Exception ex)
        {
            Log.Error("Host crashed during startup or run", ex);
        }
    }

    private static bool OtherHelpersRunning()
    {
        var self = Environment.ProcessId;
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "RalFsr.Host");
        return Process.GetProcessesByName(name).Any(p => { using (p) return p.Id != self; });
    }

    private static void StopOtherHelpers()
    {
        var self = Environment.ProcessId;
        foreach (var other in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "RalFsr.Host")))
        {
            using (other)
            {
                if (other.Id == self) continue;
                try
                {
                    Log.Info($"Closing an older helper (pid {other.Id})");
                    other.Kill();
                    other.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Couldn't close helper {other.Id}: {ex.Message}");
                }
            }
        }
    }
}
