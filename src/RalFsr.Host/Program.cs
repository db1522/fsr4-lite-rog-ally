using RalFsrHelper.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace RalFsrHelper.Host;

/// <summary>
/// Full-trust helper inside the widget's package. The widget launches it, it connects back over an app service,
/// does all file work (detect, download, install, remove) and exits when the widget closes.
/// </summary>
internal static class Program
{
    public const string ServiceName = "com.rogallylife.fsr4lite";

    private static readonly TaskCompletionSource Closed = new();

    private static async Task Main()
    {
        using var mutex = new Mutex(true, @"Local\RalFsr4LiteHost", out var first);
        if (!first) return; // one helper at a time

        AppPaths.Ensure();
        Log.Info("Host started");

        var service = new HostService();
        var connection = new AppServiceConnection
        {
            AppServiceName = ServiceName,
            PackageFamilyName = Package.Current.Id.FamilyName,
        };

        connection.RequestReceived += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var message = e.Request.Message;
                var args = message.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
                var cmd = args.TryGetValue("cmd", out var c) ? c : "";

                string json;
                try { json = await service.HandleAsync(cmd, args); }
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

        connection.ServiceClosed += (_, _) => Closed.TrySetResult();

        var status = await connection.OpenAsync();
        if (status != AppServiceConnectionStatus.Success)
        {
            Log.Error($"App service connection failed: {status}");
            return;
        }

        await Closed.Task;
        Log.Info("Widget closed, host exiting");
    }
}
