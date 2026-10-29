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

    /// <summary>
    /// Runs the helper on a thread with a Windows message loop. Windows components the helper uses (the app service
    /// connection, notifications) can create hidden windows, and Windows sends some messages to every window and waits
    /// for an answer: device and driver installs, settings changes. A helper that never pumped messages showed up as
    /// "RalFsr.Host.exe stopped interacting with Windows", and on the ROG Xbox Ally X that stalled the Xbox Service
    /// controller driver install (error 1722), which stopped the Xbox app opening.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        // Anything that goes wrong before normal logging is up still leaves a trace in helper.log (or, if even that
        // can't be written, in a crash file in the temp folder).
        // Never show a crash dialog: one left open blocks Windows from restarting or shutting down ("This app is
        // preventing you from restarting"). Any error is logged instead, and the helper exits quietly.
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => QuietExit("Unhandled error", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Log.Warn($"Background task error (ignored): {e.Exception.GetBaseException().Message}"); } catch { }
            e.SetObserved();
        };
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            try { Log.Warn($"Message loop error (ignored): {e.Exception.Message}"); } catch { }
        };
        try
        {
            AppPaths.Ensure();
            Log.Info($"Helper process started ({(args.Length == 0 ? "watcher" : string.Join(" ", args))}, " +
                     $"version {SelfUpdate.Display(SelfUpdate.Installed)})");
        }
        catch (Exception ex)
        {
            EarlyCrash(ex);
            throw;
        }
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        var loop = new System.Windows.Forms.ApplicationContext();
        var context = new System.Windows.Forms.WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);

        context.Post(async _ =>
        {
            try
            {
                if (args.Contains("--widget", StringComparer.OrdinalIgnoreCase)) await RunForWidgetAsync();
                else await RunWatcherOnlyAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Helper stopped with an error", ex);
            }
            finally
            {
                loop.ExitThread();
            }
        }, null);

        // When Windows asks programs to close (Restart Manager during an install such as Xbox Service's repair, sign-out,
        // shutdown), exit straight away. The helper has nothing to save, and staying put is what made the Xbox Service
        // installer time out ("HangType: Quiesce"). It starts again the next time the widget opens.
        // Windows is signing out, restarting or shutting down: stop on the spot. A normal exit runs shutdown code on
        // other threads (connections closing, background work), and an error there would block the restart.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, e) =>
        {
            try { Log.Info($"Windows asked the helper to close ({e.Reason}); exiting"); } catch { }
            Process.GetCurrentProcess().Kill();
        };

        System.Windows.Forms.Application.Run(loop); // pumps messages until the helper is done
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

    /// <summary>Logs an unexpected error and ends the helper without a crash dialog (the widget restarts it when needed).</summary>
    private static void QuietExit(string what, Exception? ex)
    {
        try { Log.Error($"{what}; the helper is closing", ex ?? new Exception("unknown error")); } catch { }
        try { Process.GetCurrentProcess().Kill(); } catch { Environment.Exit(1); }
    }

    private const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    private static void EarlyCrash(Exception? ex)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [CRASH] Helper stopped during startup: {ex}{Environment.NewLine}";
        try { Log.Error("Helper stopped during startup", ex ?? new Exception("unknown error")); return; }
        catch { }
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "RalFsr.Host-crash.log"), text); }
        catch { }
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
        DateTime myStart;
        try { using var me = Process.GetCurrentProcess(); myStart = me.StartTime; }
        catch { myStart = DateTime.Now; }

        foreach (var other in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "RalFsr.Host")))
        {
            using (other)
            {
                if (other.Id == self) continue;

                // Only close helpers that really are older. If two start in the same moment, they'd otherwise close
                // each other; the one with the lower process id steps aside instead.
                DateTime otherStart;
                try { otherStart = other.StartTime; } catch { otherStart = DateTime.MinValue; }
                var sameMoment = Math.Abs((otherStart - myStart).TotalSeconds) < 2;
                if (otherStart > myStart || (sameMoment && other.Id > self))
                {
                    Log.Info($"A newer helper is starting (pid {other.Id}); this one exits");
                    Environment.Exit(0);
                }

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
