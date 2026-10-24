using System.Runtime.InteropServices;
using System.Text;

namespace RalFsrHelper.Host;

/// <summary>
/// Finds the game sitting under the Game Bar overlay. Game Bar is on top while the widget is open, so this walks
/// top-level windows in Z-order, skips Game Bar and shell windows, and returns the first large window that
/// belongs to a real program.
/// </summary>
public static class ForegroundGame
{
    public sealed record Window(int ProcessId, string ExePath, string Title);

    private static readonly HashSet<string> SkipExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameBar.exe", "GameBarFTServer.exe", "GameBarElevatedFT_Alias.exe", "XboxGameBarWidgets.exe",
        "ApplicationFrameHost.exe", "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "SearchApp.exe", "TextInputHost.exe", "LockApp.exe", "SystemSettings.exe",
        "RalFsr.Host.exe", "RalFsr.Widget.exe", "ArmouryCrateSE.exe", "ArmouryCrate.exe", "steamwebhelper.exe",
        "Taskmgr.exe", "dwm.exe",
    };

    public static Window? Find()
    {
        Window? found = null;
        var self = Environment.ProcessId;
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidate(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == self) return true;

            var exe = ProcessPath((int)pid);
            if (exe is null) return true;
            if (SkipExes.Contains(Path.GetFileName(exe))) return true;
            if (exe.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) return true;

            found = new Window((int)pid, exe, Title(hwnd));
            return false; // stop at the top-most match
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>Full exe path of a process, or null if it can't be read.</summary>
    public static string? ProcessPath(int pid)
    {
        IntPtr handle;
        try { handle = OpenProcess(ProcessQueryLimitedInformation, false, pid); }
        catch { return null; }
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>True when any running process was started from an exe inside this folder.</summary>
    public static bool AnyRunningFrom(string folder)
    {
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                var path = ProcessPath(p.Id);
                if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static bool IsCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;

        // Games fill most of the screen, windowed or not. Skip small utility windows.
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        long screen = (long)(info.rcMonitor.Right - info.rcMonitor.Left) * (info.rcMonitor.Bottom - info.rcMonitor.Top);
        long window = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
        return screen > 0 && window >= screen * 4 / 10;
    }

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const int GwOwner = 4;
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
