using System.Diagnostics;
using System.Runtime.InteropServices;
using RalFsrHelper.Core;

namespace RalFsrHelper.Host;

/// <summary>
/// Reads which DLLs a running game has loaded, to tell whether OptiScaler and the FSR 4 library are really in use.
/// </summary>
public static class LoadedModules
{
    public enum Stage
    {
        /// <summary>Game isn't running, or FSR 4.1 Lite isn't installed in it.</summary>
        None,

        /// <summary>Installed, but the running game started before the install. Needs a restart.</summary>
        NeedsRestart,

        /// <summary>The game started after the install and still didn't load OptiScaler: it ignores this loader name.</summary>
        NotLoaded,

        /// <summary>OptiScaler is loaded but no FSR 4 library yet, usually because FSR is off in the game.</summary>
        WaitingForFsr,

        /// <summary>FSR 4 is running from the widget's own file, so the Lite model is the one in use.</summary>
        Active,

        /// <summary>FSR 4 is running, but from a copy without the Lite model (usually the AMD driver's own).</summary>
        NotLite,

        /// <summary>The game's upscaler started through OptiScaler, but no FSR 4 file loaded: it's running FSR 3.</summary>
        Fsr3Fallback,
    }

    /// <param name="Confirmed">OptiScaler's log shows the Lite model being hooked, not just the file loading.</param>
    public sealed record Status(Stage Stage, string Api, bool Confirmed = false, string Detail = "")
    {
        /// <summary>Latest render and display size from OptiScaler's log (0 when not known yet).</summary>
        public int RenderWidth { get; init; }
        public int RenderHeight { get; init; }
        public int DisplayWidth { get; init; }
        public int DisplayHeight { get; init; }

        public bool HasResolution => RenderWidth > 0 && DisplayWidth > 0;
        public bool AtFullResolution => HasResolution && RenderWidth >= DisplayWidth;
    }

    public static Status Check(int processId, string exeDir, InstallManifest manifest)
    {
        var modules = List(processId);
        if (modules.Count == 0) return new Status(Stage.None, "");

        // Match by file name, not by full path. Xbox app / Game Pass games can report the same folder under
        // C:\XboxGames\... and under C:\Program Files\WindowsApps\..., so an exact path comparison misses them.
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var proxyLoaded = modules.Any(m =>
            Path.GetFileName(m).Equals(manifest.ProxyName, StringComparison.OrdinalIgnoreCase) &&
            !m.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase));
        if (!proxyLoaded)
        {
            var startedAfterInstall = StartTime(processId) is DateTime started && started > manifest.InstalledAt;
            return new Status(startedAfterInstall ? Stage.NotLoaded : Stage.NeedsRestart, "");
        }

        var log = ReadOptiLog(exeDir);
        var res = ParseResolution(log);

        foreach (var (file, api) in new[] { (InstallConfig.Dx12Fsr4File, "DX12"), (InstallConfig.VulkanFsr4File, "Vulkan") })
        {
            var loaded = modules.FirstOrDefault(m => Path.GetFileName(m).Equals(file, StringComparison.OrdinalIgnoreCase));
            if (loaded is null) continue;

            // Only the widget's own copy (in the game's OptiScaler folder) carries the Lite model. OptiScaler uses any
            // copy that's already in memory first, so a copy the AMD driver loaded earlier wins and has no Lite.
            // Compared by its folder, not the full path, for the same XboxGames / WindowsApps reason as above.
            var parent = Path.GetFileName(Path.GetDirectoryName(loaded) ?? "");
            var ours = parent.Equals(InstallConfig.OptiSubfolder, StringComparison.OrdinalIgnoreCase) &&
                       !loaded.Contains("DriverStore", StringComparison.OrdinalIgnoreCase);
            if (!ours)
                return res(new Status(Stage.NotLite, api, Detail: loaded));

            var liteHooked = log.Contains("ffxProvider_FSR4Lite_Int8", StringComparison.Ordinal);
            return res(new Status(Stage.Active, api, Confirmed: liteHooked));
        }

        if (log.Contains("can't run any FSR 4 model", StringComparison.Ordinal) ||
            log.Contains("doesn't support FSR 4", StringComparison.Ordinal))
            return res(new Status(Stage.NotLite, "", Detail: "OptiScaler reports the GPU can't run FSR 4 here"));

        // The upscaler was created (OptiScaler logged its resolutions) but no FSR 4 file is loaded: FSR 3 is running.
        var probe = res(new Status(Stage.WaitingForFsr, ""));
        return probe.HasResolution ? probe with { Stage = Stage.Fsr3Fallback } : probe;
    }

    private static readonly System.Text.RegularExpressions.Regex FeatureLine = new(
        @"Render Resolution: (\d+)x(\d+), Display Resolution (\d+)x(\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex WidthLine = new(
        @"renderWidth: (\d+), maxWidth: (\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Streamline (DLSS in many newer games) logs the size of the image the game really hands over each frame. A game
    /// can declare a small render size yet still render at full size: Forza Horizon 6 logs "(800,450)(optimal)" but
    /// "color_in extents (0,0,1600,900)". The extents are the truth.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ColorInLine = new(
        @"color_in extents \((\d+),(\d+),(\d+),(\d+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ColorOutLine = new(
        @"color_out extents \((\d+),(\d+),(\d+),(\d+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The game's output height from its last OptiScaler.log (Streamline's color_out extents, else OptiScaler's
    /// "Display Resolution"), or null before the game has run with OptiScaler. Used to size the overlay.
    /// </summary>
    public static int? LastDisplayHeight(string exeDir)
    {
        try
        {
            var path = Path.Combine(exeDir, "OptiScaler.log");
            if (!File.Exists(path)) return null;
            string log;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                log = reader.ReadToEnd();

            var outExtents = ColorOutLine.Matches(log);
            if (outExtents.Count > 0) return int.Parse(outExtents[^1].Groups[4].Value);
            var display = System.Text.RegularExpressions.Regex.Matches(log, @"Display Resolution:?\s*(\d+)x(\d+)");
            if (display.Count > 0) return int.Parse(display[^1].Groups[2].Value);
        }
        catch
        {
            // a log that can't be read just means "unknown"
        }
        return null;
    }

    /// <summary>Returns a function that stamps the latest logged render/display size onto a status.</summary>
    private static Func<Status, Status> ParseResolution(string log)
    {
        int rw = 0, rh = 0, dw = 0, dh = 0;
        var feature = FeatureLine.Matches(log);
        if (feature.Count > 0)
        {
            var m = feature[^1];
            rw = int.Parse(m.Groups[1].Value); rh = int.Parse(m.Groups[2].Value);
            dw = int.Parse(m.Groups[3].Value); dh = int.Parse(m.Groups[4].Value);
        }

        // Newer upscaler contexts log the width pair; use it when it comes later than the last feature line.
        var widths = WidthLine.Matches(log);
        if (widths.Count > 0 && (feature.Count == 0 || widths[^1].Index > feature[^1].Index))
        {
            var m = widths[^1];
            var newRw = int.Parse(m.Groups[1].Value);
            var newDw = int.Parse(m.Groups[2].Value);
            if (dw > 0 && newDw == dw && rw > 0) rh = (int)Math.Round((double)rh * newRw / rw);
            else { rh = 0; dh = 0; }
            rw = newRw; dw = newDw;
        }

        // The real input size from Streamline wins over any declared size logged before it.
        var colorIn = ColorInLine.Matches(log);
        if (colorIn.Count > 0)
        {
            var m = colorIn[^1];
            rw = int.Parse(m.Groups[3].Value); rh = int.Parse(m.Groups[4].Value);
            var colorOut = ColorOutLine.Matches(log);
            if (colorOut.Count > 0)
            {
                var o = colorOut[^1];
                dw = int.Parse(o.Groups[3].Value); dh = int.Parse(o.Groups[4].Value);
            }
        }

        return status => status with { RenderWidth = rw, RenderHeight = rh, DisplayWidth = dw, DisplayHeight = dh };
    }

    private static DateTime? StartTime(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return p.StartTime;
        }
        catch
        {
            return null; // protected process: assume it may need a restart
        }
    }

    /// <summary>OptiScaler.log from this session (next to the game exe). Empty when logging is off or the file is busy.</summary>
    private static string ReadOptiLog(string exeDir)
    {
        var path = Path.Combine(exeDir, "OptiScaler.log");
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return text.Length > 2_000_000 ? text[..2_000_000] : text; // the lines that matter are near the top
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// The game's graphics API from the system libraries it has loaded: "DX12", "Vulkan" or "DX11" (null if unknown).
    /// DX12 games often load d3d11.dll too, so DX12 is checked first.
    /// </summary>
    public static string? GraphicsApi(int processId)
    {
        var names = List(processId).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return null;
        if (names.Contains("d3d12.dll")) return "DX12";
        if (names.Contains("vulkan-1.dll")) return "Vulkan";
        if (names.Contains("d3d11.dll")) return "DX11";
        return null;
    }

    /// <summary>Full paths of every module in the process. Empty if it can't be read.</summary>
    public static List<string> List(int processId)
    {
        var result = new List<string>();
        var snapshot = CreateToolhelp32Snapshot(SnapModule | SnapModule32, (uint)processId);
        if (snapshot == InvalidHandle) return result;
        try
        {
            var entry = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (!Module32FirstW(snapshot, ref entry)) return result;
            do
            {
                result.Add(entry.szExePath);
                entry.dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>();
            } while (Module32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private const uint SnapModule = 0x00000008;
    private const uint SnapModule32 = 0x00000010;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32FirstW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32NextW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
