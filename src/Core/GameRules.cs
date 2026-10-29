namespace RalFsrHelper.Core;

/// <summary>
/// Setups the OptiScaler community has worked out for specific games, applied automatically. Keyed by the game's exe
/// name. Loader: the OptiScaler file name to install as (when it's free in the folder). Tip: a line for the game card.
/// </summary>
public static class GameRules
{
    private sealed record Rule(string Loader, Func<string?> Tip);

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        // OptiScaler wiki, Red Dead Redemption II: works as winmm.dll or version.dll (dxgi.dll crashes it at startup),
        // with FSR 2 inputs on DirectX 12. Vulkan, the game's default, needs an ASI-loader setup the widget doesn't do.
        ["RDR2.exe"] = new Rule("winmm.dll", Rdr2Tip),
    };

    /// <summary>The loader name a game needs, or null for the default order.</summary>
    public static string? LoaderFor(string? exeName) =>
        exeName is not null && Rules.TryGetValue(Path.GetFileName(exeName), out var r) ? r.Loader : null;

    /// <summary>A setup tip for the game card (starting with a space), or "".</summary>
    public static string TipFor(string? exeName)
    {
        if (exeName is null || !Rules.TryGetValue(Path.GetFileName(exeName), out var r)) return "";
        try { return r.Tip() is { Length: > 0 } tip ? " " + tip : ""; }
        catch { return ""; }
    }

    private static string? Rdr2Tip()
    {
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Rockstar Games", "Red Dead Redemption 2", "Settings", "system.xml");
        var vulkan = File.Exists(settings) &&
                     File.ReadAllText(settings).Contains("kSettingAPI_Vulkan", StringComparison.OrdinalIgnoreCase);
        return vulkan
            ? "Red Dead Redemption 2 is set to Vulkan, which crashes with OptiScaler. In the game, go to Settings > " +
              "Graphics > Advanced Settings, set Graphics API to DirectX 12, then pick FSR 2 as the upscaler."
            : "In Red Dead Redemption 2, use DirectX 12 and pick FSR 2 as the upscaler.";
    }
}
