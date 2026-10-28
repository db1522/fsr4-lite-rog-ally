using System.Text.Json;

namespace RalFsrHelper.Core;

public sealed class AppSettings
{
    /// <summary>Shows OptiScaler's FPS overlay with the live render to output resolution.</summary>
    public bool ShowResolutionOverlay { get; set; }

    /// <summary>
    /// Retired: AMD's on-screen FSR 4 watermark. Notifications replace it, and the switch is gone from the widget,
    /// so it's always off (an older setting of true is ignored and cleared on load).
    /// </summary>
    public bool ShowFsr4Watermark { get; set; }

    /// <summary>A notification when FSR 4.1 Lite is confirmed active (or isn't running) in a game. Starts with Windows.</summary>
    public bool NotifyWhenActive { get; set; } = true;

    /// <summary>
    /// Update installed games to each new OptiScaler build automatically (games that aren't running; each keeps its own
    /// settings). On by default. Off: the widget offers "Update N games" and a notification instead.
    /// </summary>
    public bool AutoUpdateGames { get; set; } = true;

    /// <summary>Size of the in-game FPS and resolution overlay: small, medium (default), large or xlarge.</summary>
    public string OverlaySize { get; set; } = "medium";

    /// <summary>Corner for the in-game overlay: topleft (default), topright, bottomleft or bottomright.</summary>
    public string OverlayPosition { get; set; } = "topleft";

    /// <summary>The OptiScaler build the "updates ready" notification was last shown for, so it's shown once per build.</summary>
    public string LastUpdateNoticeBuild { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
                loaded.ShowFsr4Watermark = false; // retired setting, see above
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        AppPaths.Ensure();
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
    }
}
