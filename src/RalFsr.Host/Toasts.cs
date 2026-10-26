using System.Security;
using RalFsrHelper.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace RalFsrHelper.Host;

/// <summary>Windows notifications from the helper, shown with the widget's name and ROG Ally Life icon.</summary>
public static class Toasts
{
    public static void Show(string title, params string[] lines)
    {
        try
        {
            var body = string.Concat(lines.Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => $"<text>{SecurityElement.Escape(l)}</text>"));
            var xml = new XmlDocument();
            xml.LoadXml(
                // Clicking opens Game Bar (where the widget lives) instead of launching the widget's app on its own,
                // which showed its splash screen and a full-screen window.
                "<toast activationType=\"protocol\" launch=\"ms-gamebar:\"><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text>{body}" +
                "</binding></visual><audio silent=\"true\"/></toast>");

            // The helper runs inside the widget's package, so notifications belong to the widget app ("App").
            var appId = Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
            ToastNotificationManager.CreateToastNotifier(appId).Show(new ToastNotification(xml));
            Log.Info($"Notification: {title} | {string.Join(" | ", lines)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't show a notification: {ex.Message}");
        }
    }
}
