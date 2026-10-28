using System.Management;

namespace RalFsrHelper.Core;

/// <summary>The handheld's screen height in pixels (landscape).</summary>
public sealed record ScreenInfo(int HeightPx);

/// <summary>
/// Reads the screen's current resolution once, through WMI (Win32_VideoController), to size the overlay for games that
/// haven't run with FSR 4.1 Lite yet. Handheld panels are often portrait-native, so the smaller value is the landscape
/// height. Falls back to 1080 when nothing can be read.
/// </summary>
public static class ScreenProbe
{
    public const int DefaultHeightPx = 1080;

    private static ScreenInfo? _cached;
    private static readonly object Gate = new();

    public static ScreenInfo Get()
    {
        lock (Gate)
        {
            return _cached ??= new ScreenInfo(ReadHeightPx() ?? DefaultHeightPx);
        }
    }

    private static int? ReadHeightPx()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    if (int.TryParse(obj["CurrentHorizontalResolution"]?.ToString(), out var w) &&
                        int.TryParse(obj["CurrentVerticalResolution"]?.ToString(), out var h) && w > 0 && h > 0)
                        return Math.Min(w, h);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read the screen resolution: {ex.Message}");
        }
        return null;
    }
}
