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

        private static async Task<bool> LaunchAsync()
        {
            if (App.Connection != null) return true;

            _connected = new TaskCompletionSource<bool>();
            App.ConnectionChanged += OnConnectionChanged;
            try
            {
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("Widget"); // --widget
                // Generous: the first start after an install or update unpacks the helper before it can connect.
                await Task.WhenAny(_connected.Task, Task.Delay(TimeSpan.FromSeconds(45)));
                return App.Connection != null;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                App.ConnectionChanged -= OnConnectionChanged;
            }
        }

        private static void OnConnectionChanged(object sender, EventArgs e)
        {
            if (App.Connection != null) _connected?.TrySetResult(true);
        }

        /// <param name="pairs">Alternating key, value strings.</param>
        public static async Task<JsonObject> SendAsync(string cmd, params string[] pairs)
        {
            if (!await EnsureConnectedAsync())
                return Error("The helper didn't start. Close the widget and open it again. " +
                             "If this keeps happening, restart the Ally and send ROG Ally Life the helper.log file.");

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
