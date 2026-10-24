using System.Management;

namespace RalFsrHelper.Core;

public enum DeviceSupport
{
    /// <summary>ROG Ally or ROG Ally X (Ryzen Z1 / Z1 Extreme). The app's target.</summary>
    Supported,

    /// <summary>ROG Xbox Ally X / X20 (Ryzen AI Z2 Extreme). ASUS ships FSR 4.1 officially.</summary>
    OfficialDriver,

    /// <summary>Anything else. Blocked unless started with --any-device.</summary>
    Unsupported,
}

public sealed record DeviceInfo(string Model, string Cpu, DeviceSupport Support, string Message)
{
    public string Summary => $"{Model} with {Cpu}";
}

public static class DeviceCheck
{
    public static DeviceInfo Detect()
    {
        var cpu = (QueryFirst("SELECT Name FROM Win32_Processor", "Name") ?? "unknown processor").Trim();
        var model = (QueryFirst("SELECT Model FROM Win32_ComputerSystem", "Model") ?? "Unknown device").Trim();
        var c = cpu.ToLowerInvariant();

        if (c.Contains("z2 extreme"))
        {
            return new DeviceInfo(model, cpu, DeviceSupport.OfficialDriver,
                "This handheld gets FSR 4.1 from ASUS, so it doesn't need this tool. In Armoury Crate SE, go to " +
                "Update Center > Preview Updates and install the GPU driver, then turn on FSR 4.1 in Command Center.");
        }

        if (c.Contains("ryzen z1"))
        {
            return new DeviceInfo(model, cpu, DeviceSupport.Supported,
                "Supported. FSR 4 Lite runs through OptiScaler on this handheld.");
        }

        return new DeviceInfo(model, cpu, DeviceSupport.Unsupported,
            "This tool is built and tested for the ROG Ally and ROG Ally X (Ryzen Z1 and Z1 Extreme). " +
            "It hasn't been tested on this device, so installs are turned off.");
    }

    private static string? QueryFirst(string wql, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var value = obj[property]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"WMI query failed ({wql}): {ex.Message}");
        }
        return null;
    }
}
