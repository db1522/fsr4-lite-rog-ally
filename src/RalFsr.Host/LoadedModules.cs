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

        /// <summary>The FSR 4.1.2 library is loaded in the game.</summary>
        Active,
    }

    public sealed record Status(Stage Stage, string Api);

    public static Status Check(int processId, string exeDir, InstallManifest manifest)
    {
        var modules = List(processId);
        if (modules.Count == 0) return new Status(Stage.None, "");

        var proxy = Path.Combine(exeDir, manifest.ProxyName);
        if (!modules.Any(m => m.Equals(proxy, StringComparison.OrdinalIgnoreCase)))
            return new Status(Stage.NeedsRestart, "");

        foreach (var (file, api) in new[] { (InstallConfig.Dx12Fsr4File, "DX12"), (InstallConfig.VulkanFsr4File, "Vulkan") })
        {
            var loaded = modules.FirstOrDefault(m => Path.GetFileName(m).Equals(file, StringComparison.OrdinalIgnoreCase));
            if (loaded is null) continue;

            // Ours (from the game's OptiScaler folder), or any copy new enough to carry the Lite model.
            var ours = Path.Combine(exeDir, InstallConfig.OptiSubfolder, file);
            if (loaded.Equals(ours, StringComparison.OrdinalIgnoreCase) || IsNewEnough(loaded))
                return new Status(Stage.Active, api);
        }

        return new Status(Stage.WaitingForFsr, "");
    }

    private static bool IsNewEnough(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart) >= Fsr4Library.MinimumDx12Version;
        }
        catch
        {
            return false;
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
