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
        /// <summary>Game isn't running, or FSR 4 Lite isn't installed in it.</summary>
        None,

        /// <summary>Installed, but the running game started before the install. Needs a restart.</summary>
        NeedsRestart,

        /// <summary>OptiScaler is loaded but no FSR 4 library yet, usually because FSR is off in the game.</summary>
        WaitingForFsr,

        /// <summary>FSR 4 is running from the widget's own file, so the Lite model is the one in use.</summary>
        Active,

        /// <summary>FSR 4 is running, but from a copy without the Lite model (usually the AMD driver's own).</summary>
        NotLite,
    }

    /// <param name="Confirmed">OptiScaler's log shows the Lite model being hooked, not just the file loading.</param>
    public sealed record Status(Stage Stage, string Api, bool Confirmed = false, string Detail = "");

    public static Status Check(int processId, string exeDir, InstallManifest manifest)
    {
        var modules = List(processId);
        if (modules.Count == 0) return new Status(Stage.None, "");

        var proxy = Path.Combine(exeDir, manifest.ProxyName);
        if (!modules.Any(m => m.Equals(proxy, StringComparison.OrdinalIgnoreCase)))
            return new Status(Stage.NeedsRestart, "");

        var log = ReadOptiLog(exeDir);

        foreach (var (file, api) in new[] { (InstallConfig.Dx12Fsr4File, "DX12"), (InstallConfig.VulkanFsr4File, "Vulkan") })
        {
            var loaded = modules.FirstOrDefault(m => Path.GetFileName(m).Equals(file, StringComparison.OrdinalIgnoreCase));
            if (loaded is null) continue;

            // Only the widget's own copy (in the game's OptiScaler folder) carries the Lite model. OptiScaler uses any
            // copy that's already in memory first, so a copy the AMD driver loaded earlier wins and has no Lite.
            var ours = Path.Combine(exeDir, InstallConfig.OptiSubfolder, file);
            if (!loaded.Equals(ours, StringComparison.OrdinalIgnoreCase))
                return new Status(Stage.NotLite, api, Detail: loaded);

            var liteHooked = log.Contains("ffxProvider_FSR4Lite_Int8", StringComparison.Ordinal);
            return new Status(Stage.Active, api, Confirmed: liteHooked);
        }

        if (log.Contains("can't run any FSR 4 model", StringComparison.Ordinal) ||
            log.Contains("doesn't support FSR 4", StringComparison.Ordinal))
            return new Status(Stage.NotLite, "", Detail: "OptiScaler reports the GPU can't run FSR 4 here");

        return new Status(Stage.WaitingForFsr, "");
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
