using System.Text.Json;

namespace RalFsrHelper.Core;

public sealed class AppSettings
{
    /// <summary>Shows OptiScaler's FPS overlay with the live render to output resolution.</summary>
    public bool ShowResolutionOverlay { get; set; }

    /// <summary>Shows AMD's FSR 4 watermark on screen while FSR 4 is running.</summary>
    public bool ShowFsr4Watermark { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
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
