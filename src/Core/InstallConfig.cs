namespace RalFsrHelper.Core;

/// <summary>
/// Every OptiScaler.ini value the app writes, in one place.
/// These replace the in-game menu steps (upscaler choice and FSR 4 model), so the game starts on FSR 4.1 Lite
/// without the user opening the OptiScaler overlay.
/// </summary>
public static class InstallConfig
{
    public sealed record IniValue(string Section, string Key, string Value);

    /// <summary>
    /// Vulkan games go through OptiScaler's DX12 bridge (ffx_12). OptiScaler's native Vulkan path lists the driver
    /// FSR 4 after the SDK's FSR 3 versions, so its default pick there is FSR 3. The DX12 list puts FSR 4.1.2 first,
    /// so the bridge reliably runs FSR 4.1 Lite (from amdxcffx64.dll), at a small interop cost.
    /// </summary>
    public static IniValue VulkanUpscaler(bool hasVulkanFile) => new("Upscalers", "VulkanUpscaler", "ffx_12");

    public static IReadOnlyList<IniValue> Base(bool hasVulkanFile) => new[]
    {
        // Route the game's upscaler through FFX so FSR 4 is available.
        new IniValue("Upscalers", "Dx12Upscaler", "ffx"),
        new IniValue("Upscalers", "Dx11Upscaler", "ffx_12"),
        VulkanUpscaler(hasVulkanFile),

        // Force INT8 Lite support on the Z1 / Z1 Extreme and pick the Lite model.
        new IniValue("FSR", "Fsr4GpuSupport", "4"),
        new IniValue("FSR", "Fsr4Model", "3"),

        // Upscaling only. OptiScaler frame generation stays off by default (crash reports in Forza Horizon 6).
        new IniValue("FrameGen", "Enabled", "false"),

        // Info-level log (OptiScaler.log next to the game exe, fresh each session). The widget reads it to confirm
        // which amdxcffx64.dll was loaded and that the Lite model was hooked. Info is light; Trace is the heavy one.
        new IniValue("Log", "LogToFile", "true"),
        new IniValue("Log", "LogLevel", "2"),

        // No startup message or watermark.
        new IniValue("Menu", "DisableSplash", "true"),
        new IniValue("FSR", "Fsr4EnableWatermark", "false"),
    };

    /// <summary>
    /// OptiScaler's FPS overlay, sized for a 7" 1080p handheld: the "Simple" layout (FPS row with the upscaler name,
    /// e.g. "FSR3 -> FSR 4.1.2", plus the render -> output resolution row) at 0.8 scale in the top left corner.
    /// The "Detailed" layout at automatic scale runs off the ROG Ally's screen.
    /// </summary>
    public static IReadOnlyList<IniValue> Overlay(bool on) => new[]
    {
        new IniValue("Menu", "ShowFps", on ? "true" : "auto"),
        new IniValue("Menu", "FpsOverlayType", on ? "1" : "auto"),
        new IniValue("Menu", "FpsOverlayShowRatio", on ? "true" : "auto"),
        new IniValue("Menu", "FpsScale", on ? "0.8" : "auto"),
        new IniValue("Menu", "FpsOverlayPos", on ? "0" : "auto"),
        new IniValue("Menu", "FpsOverlayHorizontal", on ? "false" : "auto"),
    };

    /// <summary>
    /// Per-game render resolution, through OptiScaler's upscale ratio override. Every render-resolution request from
    /// the game gets this ratio, whichever quality preset the game asks for. Fixes games that stay on Native AA
    /// (Forza Horizon 6). "game" leaves it to the game's own setting.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Ratio)> RenderScales = new[]
    {
        ("game", "Game decides", ""),
        ("native", "Native AA (1.0x)", "1.0"),
        ("quality", "Quality (1.5x)", "1.5"),
        ("balanced", "Balanced (1.7x)", "1.7"),
        ("performance", "Performance (2.0x)", "2.0"),
    };

    public static IReadOnlyList<IniValue> RenderScale(string key)
    {
        var ratio = RenderScales.FirstOrDefault(r => r.Key == key).Ratio;
        var on = !string.IsNullOrEmpty(ratio);
        return new[]
        {
            new IniValue("UpscaleRatio", "UpscaleRatioOverrideEnabled", on ? "true" : "auto"),
            new IniValue("UpscaleRatio", "UpscaleRatioOverrideValue", on ? ratio! : "auto"),
        };
    }

    /// <summary>
    /// Extra OptiScaler input settings for FSR 2 games, chosen at install time from the game's graphics API
    /// (DX12, DX11 or Vulkan, read from the running game) and from what the folder scan found:
    ///   FSR 2 DLL in a DX11 game     UseFsr2Dx11Inputs=true    (OptiScaler hooks DX12 FSR 2 by default)
    ///   FSR 2 DLL in a Vulkan game   UseFsr2VulkanInputs=true
    ///   no upscaler files at all     Fsr2Pattern / Fsr3Pattern=true: search the exe for built-in FSR 2 / 3. Slows
    ///                                loading a little, so it's only turned on for these games.
    /// FSR 1 can't be upgraded: it has no motion vectors or depth for FSR 4 to work with.
    /// </summary>
    public static IReadOnlyList<IniValue> Fsr2Inputs(string? api, bool hasFsr2Dll, bool noUpscalerFound)
    {
        var values = new List<IniValue>();
        if (hasFsr2Dll && api == "DX11") values.Add(new IniValue("Inputs", "UseFsr2Dx11Inputs", "true"));
        if (hasFsr2Dll && api == "Vulkan") values.Add(new IniValue("Inputs", "UseFsr2VulkanInputs", "true"));
        if (noUpscalerFound)
        {
            values.Add(new IniValue("Inputs", "Fsr2Pattern", "true"));
            values.Add(new IniValue("Inputs", "Fsr3Pattern", "true"));
        }
        return values;
    }

    /// <summary>
    /// Nothing on screen unless the user asks for it: no OptiScaler startup message ("OptiScaler - Insert for menu")
    /// and no AMD FSR 4 watermark. Notifications and the widget's badge confirm FSR 4.1 Lite instead. Written on
    /// install and re-applied to every installed game each time the widget opens.
    /// </summary>
    public static readonly IReadOnlyList<IniValue> Quiet = new[]
    {
        new IniValue("Menu", "DisableSplash", "true"),
        new IniValue("FSR", "Fsr4EnableWatermark", "false"),
    };

    /// <summary>Kept for the existing call sites; the watermark is always off (see <see cref="Quiet"/>).</summary>
    public static IReadOnlyList<IniValue> Watermark(bool on) => Quiet;

    /// <summary>Names OptiScaler can load under, in order of preference (same list as OptiScaler's setup script).</summary>
    public static readonly string[] ProxyNames =
    {
        "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll",
    };

    /// <summary>Files from the OptiScaler package that are installers or docs, not runtime files.</summary>
    public static readonly string[] SkipPackageFiles =
    {
        "setup_windows.bat", "setup_linux.sh", "remove optiscaler.bat", "remove_optiscaler.bat",
    };

    public const string Dx12Fsr4File = "amdxcffx64.dll";
    public const string VulkanFsr4File = "amdvkffx64.dll";

    /// <summary>OptiScaler v10 reads the FSR 4 driver files from this subfolder next to the game exe.</summary>
    public const string OptiSubfolder = "OptiScaler";

    /// <summary>Hidden folder next to the game exe holding the manifest and backups for revert.</summary>
    public const string MetaFolder = ".ralfsr";
}
