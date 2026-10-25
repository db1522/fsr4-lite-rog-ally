using System.Management;

namespace RalFsrHelper.Core;

public enum DeviceSupport
{
    /// <summary>AMD RDNA 3 / 3.5 graphics (Z1, Z1 Extreme, Z2, Z2 Extreme, Ryzen 7040/8040, Ryzen AI 300).</summary>
    Supported,

    /// <summary>Z2 Extreme in a ROG Xbox Ally X / X20: ASUS ships FSR 4.1 officially, but only for FSR 3.1+ games.</summary>
    OfficialDriver,

    /// <summary>AMD RDNA 2 graphics (Z2 A, Z2 Go, Steam Deck, 6000-series APUs). Lite runs; the GPU may not gain FPS.</summary>
    Experimental,

    /// <summary>No AMD Radeon graphics (Intel, NVIDIA). FSR 4's INT8 models need an AMD GPU.</summary>
    Unsupported,
}

public sealed record DeviceInfo(string Model, string Cpu, string Gpu, DeviceSupport Support, string Message, bool IsAsus)
{
    public string Summary => $"{Model} with {Cpu}";
    public bool CanInstall => Support != DeviceSupport.Unsupported;

    /// <summary>How to open Game Bar on this handheld, for the widget's hints.</summary>
    public string GameBarHint => IsAsus ? "press the Armoury Crate button to open Game Bar" : "press Win + G to open Game Bar";
}

/// <summary>Decides support by GPU family, so any AMD Radeon handheld works, not just the ROG Ally line.</summary>
public static class DeviceCheck
{
    private static readonly string[] Rdna3Apus =
    {
        "ryzen z1", "ryzen z2 extreme", "ryzen ai z2 extreme", "ryzen z2 ", "ryzen ai", // handheld + Strix/Krackan
        "7840u", "7640u", "7540u", "7840hs", "7640hs", "8840u", "8640u", "8540u", "8840hs", "8640hs", "8845hs",
        "8945hs", "7940hs", "8040",
    };

    private static readonly string[] Rdna2Apus =
    {
        "ryzen z2 a", "ryzen z2 go", "amd custom apu 0405", "amd custom apu 0932", // Xbox Ally, Legion Go S, Steam Deck
        "6800u", "6600u", "6800hs", "6900hs", "7735u", "7735hs", "7736u",
    };

    private static readonly string[] Rdna3Gpus = { "780m", "760m", "740m", "890m", "880m", "860m", "8060s", "8050s" };
    private static readonly string[] Rdna2Gpus = { "680m", "660m" };

    public static DeviceInfo Detect()
    {
        var cpu = (QueryFirst("SELECT Name FROM Win32_Processor", "Name") ?? "unknown processor").Trim();
        var maker = (QueryFirst("SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer") ?? "").Trim();
        var model = DeviceName(maker);
        var gpu = QueryAll("SELECT Name FROM Win32_VideoController", "Name")
            .FirstOrDefault(n => n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                 n.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            ?? QueryFirst("SELECT Name FROM Win32_VideoController", "Name") ?? "unknown graphics";
        return Classify(model, cpu, gpu.Trim(), maker);
    }

    /// <summary>The support decision itself, separate from the WMI queries so it can be tested.</summary>
    public static DeviceInfo Classify(string model, string cpu, string gpu, string maker)
    {
        var c = cpu.ToLowerInvariant();
        var g = gpu.ToLowerInvariant();
        var isAsus = maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase);
        // Some AMD chips report "AMD Custom GPU 0405" (ROG Xbox Ally, Steam Deck) rather than "Radeon".
        var amdGraphics = g.Contains("radeon") || g.Contains("amd");

        if (!amdGraphics)
        {
            return new DeviceInfo(model, cpu, gpu, DeviceSupport.Unsupported,
                "FSR 4.1 Lite needs AMD Radeon graphics. This handheld uses different graphics, so installs are turned off.",
                isAsus);
        }

        var rdna2 = Rdna2Apus.Any(c.Contains) || Rdna2Gpus.Any(g.Contains);
        if (rdna2)
        {
            return new DeviceInfo(model, cpu, gpu, DeviceSupport.Experimental,
                "Experimental on this handheld. FSR 4.1 Lite runs on its RDNA 2 graphics, but it costs more GPU time than " +
                "FSR 3, so some games may run slower. Compare both and keep whichever plays better.", isAsus);
        }

        var zTwoExtreme = c.Contains("z2 extreme");
        var xboxAlly = isAsus && zTwoExtreme && (model.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || model.Contains("RC73"));
        if (xboxAlly)
        {
            return new DeviceInfo(model, cpu, gpu, DeviceSupport.OfficialDriver,
                "ASUS offers FSR 4.1 for this handheld in Armoury Crate SE (Update Center > Preview Updates), but only " +
                "for games with FSR 3.1 or newer. This widget also covers games that use DLSS or XeSS.", isAsus);
        }

        // Everything else with Radeon graphics: RDNA 3 / 3.5 APUs by name, and unknown Radeon chips by default.
        var known = Rdna3Apus.Any(c.Contains) || Rdna3Gpus.Any(g.Contains);
        return new DeviceInfo(model, cpu, gpu, DeviceSupport.Supported,
            known ? "Supported. FSR 4.1 Lite runs through OptiScaler on this handheld."
                  : "Supported. This Radeon chip isn't on the tested list yet, but FSR 4.1 Lite should run.", isAsus);
    }

    /// <summary>A readable device name: Lenovo keeps it in SystemFamily ("Legion Go 8APU1"), others in Model.</summary>
    private static string DeviceName(string maker)
    {
        var model = (QueryFirst("SELECT Model FROM Win32_ComputerSystem", "Model") ?? "").Trim();
        var family = (QueryFirst("SELECT SystemFamily FROM Win32_ComputerSystem", "SystemFamily") ?? "").Trim();
        if (maker.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) && family.Length > 0) return "Lenovo " + family;
        if (model.Length == 0) return "Unknown device";
        return maker.Length > 0 && !model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) &&
               !maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase)
            ? $"{maker} {model}"
            : model;
    }

    private static string? QueryFirst(string wql, string property) => QueryAll(wql, property).FirstOrDefault();

    private static List<string> QueryAll(string wql, string property)
    {
        var values = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var value = obj[property]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"WMI query failed ({wql}): {ex.Message}");
        }
        return values;
    }
}
