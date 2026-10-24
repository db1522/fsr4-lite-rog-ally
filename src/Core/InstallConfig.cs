namespace RalFsrHelper.Core;

/// <summary>
/// Every OptiScaler.ini value the app writes, in one place.
/// These replace the in-game menu steps (upscaler choice and FSR 4 model), so the game starts on FSR 4 Lite
/// without the user opening the OptiScaler overlay.
/// </summary>
public static class InstallConfig
{
    public sealed record IniValue(string Section, string Key, string Value);

    /// <summary>
    /// Vulkan games: with the Vulkan FSR 4 file (amdvkffx64.dll) imported, OptiScaler's native Vulkan FFX path loads it
    /// directly. Without it, Vulkan games go through the DX12 bridge (ffx_12) and use amdxcffx64.dll instead.
    /// </summary>
    public static IniValue VulkanUpscaler(bool hasVulkanFile) =>
        new("Upscalers", "VulkanUpscaler", hasVulkanFile ? "ffx" : "ffx_12");

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
    };

    public static IReadOnlyList<IniValue> Overlay(bool on) => new[]
    {
        new IniValue("Menu", "ShowFps", on ? "true" : "auto"),
        new IniValue("Menu", "FpsOverlayType", on ? "2" : "auto"),
        new IniValue("Menu", "FpsOverlayShowRatio", on ? "true" : "auto"),
    };

    public static IReadOnlyList<IniValue> Watermark(bool on) => new[]
    {
        new IniValue("FSR", "Fsr4EnableWatermark", on ? "true" : "auto"),
    };

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
