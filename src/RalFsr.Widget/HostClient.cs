using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Data.Json;
using Windows.Foundation.Collections;

namespace RalFsr.Widget
{
    /// <summary>Starts the full-trust helper on demand and sends it commands. Every reply is one JSON object.</summary>
    internal static class HostClient
    {
        private static TaskCompletionSource<bool> _connected;

        /// <summary>The launch in progress, shared by every caller, so the helper is only ever started once at a time.</summary>
        private static Task<bool> _launching;

        public static Task<bool> EnsureConnectedAsync()
        {
            if (App.Connection != null) return Task.FromResult(true);
            if (_launching != null && !_launching.IsCompleted) return _launching;
            _launching = LaunchAsync();
            return _launching;
        }

        /// <summary>
        /// Starts the helper and waits for it to connect back, trying up to three times. Right after an update (and the
        /// Game Bar restart that ends it) Windows can still be finishing the package registration: the first launch then
        /// fails at once, or the helper never connects. A short pause and another try sorts that out by itself.
        /// </summary>
        private static async Task<bool> LaunchAsync()
        {
            if (App.Connection != null) return true;

            var waits = new[] { TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20) };
            for (var attempt = 0; attempt < waits.Length; attempt++)
            {
                if (attempt > 0)
                {
                    Starting?.Invoke(null, attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }

                _connected = new TaskCompletionSource<bool>();
                App.ConnectionChanged += OnConnectionChanged;
                try
                {
                    await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("Widget"); // --widget
                    await Task.WhenAny(_connected.Task, Task.Delay(waits[attempt]));
                    if (App.Connection != null) return true;
                    CrashLog.Write($"Starting the helper, try {attempt + 1} of {waits.Length}",
                        new TimeoutException($"The helper was launched but didn't connect within {waits[attempt].TotalSeconds:0} s."));
                }
                catch (Exception ex)
                {
                    // Launch refused (often right after an update): note why, then try again after the pause.
                    CrashLog.Write($"Starting the helper, try {attempt + 1} of {waits.Length}", ex);
                    continue;
                }
                finally
                {
                    App.ConnectionChanged -= OnConnectionChanged;
                }
            }
            return App.Connection != null;
        }

        /// <summary>Raised before a retry, with the attempt number (2 or 3), so the widget can say it's still starting.</summary>
        public static event EventHandler<int> Starting;

        private static void OnConnectionChanged(object sender, EventArgs e)
        {
            if (App.Connection != null) _connected?.TrySetResult(true);
        }

        /// <summary>
        /// Installed or updated within the last hour. Right after an update Windows can refuse to start the helper until
        /// the device restarts and finishes the switch-over to the new version.
        /// </summary>
        public static bool JustUpdated()
        {
            try { return DateTimeOffset.Now - Windows.ApplicationModel.Package.Current.InstalledDate < TimeSpan.FromHours(1); }
            catch (Exception) { return false; }
        }

        /// <param name="pairs">Alternating key, value strings.</param>
        public static async Task<JsonObject> SendAsync(string cmd, params string[] pairs)
        {
            if (!await EnsureConnectedAsync())
                return Error(JustUpdated()
                    ? "FSR 4.1 Lite was just updated, and Windows needs a restart to finish the update. Restart your " +
                      "device, then open the widget again."
                    : "The helper didn't start after three tries. Close Game Bar, open the widget again, and if it " +
                      "still doesn't start, restart the device and send ROG Ally Life the helper.log file.");

            var connection = App.Connection;
            if (connection == null) return Error("The helper disconnected. Close the widget and open it again.");

            var message = new ValueSet { ["cmd"] = cmd };
            for (var i = 0; i + 1 < pairs.Length; i += 2) message[pairs[i]] = pairs[i + 1] ?? "";

            AppServiceResponse response;
            try
            {
                response = await connection.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                return Error("Couldn't reach the helper: " + ex.Message);
            }

            if (response.Status != AppServiceResponseStatus.Success)
                return Error("The helper didn't answer (" + response.Status + "). Close the widget and open it again.");

            object raw;
            JsonObject parsed;
            if (response.Message.TryGetValue("json", out raw) && raw is string && JsonObject.TryParse((string)raw, out parsed))
                return parsed;

            return Error("The helper sent an unreadable reply.");
        }

        private static JsonObject Error(string message)
        {
            var o = new JsonObject();
            o["ok"] = JsonValue.CreateBooleanValue(false);
            o["message"] = JsonValue.CreateStringValue(message);
            return o;
        }
    }
}
