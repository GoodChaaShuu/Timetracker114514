using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TimeTracker;

/// <summary>某一时刻的前台应用快照。</summary>
/// <param name="ExeName">可执行文件名，例如 chrome.exe</param>
/// <param name="ExePath">完整路径，可能为空（无权限时）</param>
/// <param name="Title">窗口标题，可能为空（窗口提权时普通进程读不到）</param>
/// <param name="Hwnd">顶层窗口句柄，用于区分同一 exe 的多个窗口</param>
public sealed record ForegroundApp(string ExeName, string ExePath, string Title, IntPtr Hwnd)
{
    /// <summary>会话去重键：同一个 exe 的同一个窗口才算“连续使用”。</summary>
    public string Key => ExeName + "|" + Hwnd.ToString("X");
}

/// <summary>读取当前前台窗口所属的进程。</summary>
public static class Foreground
{
    /// <summary>UWP 应用的顶层窗口宿主进程，需要特殊处理。</summary>
    private const string FrameHost = "ApplicationFrameHost.exe";

    public static ForegroundApp? Current()
    {
        var hwnd = Native.GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : Resolve(hwnd);
    }

    private static ForegroundApp? Resolve(IntPtr hwnd)
    {
        uint pid = PidOf(hwnd);
        if (pid == 0) return null;

        var (name, path) = ProcessInfo(pid);
        if (name.Length == 0) return null;

        // UWP（计算器、设置、照片、商店应用…）的顶层窗口属于 ApplicationFrameHost.exe，
        // 直接取进程名会把所有 UWP 应用算成同一个。真实进程在它的子窗口里。
        if (name.Equals(FrameHost, StringComparison.OrdinalIgnoreCase))
        {
            var real = FindRealUwpChild(hwnd);
            if (real is not null) return real with { Title = WindowTitle(hwnd) };
        }

        return new ForegroundApp(name, path, WindowTitle(hwnd), hwnd);
    }

    /// <summary>在 ApplicationFrameHost 的宿主窗口里找出真正承载应用内容的子窗口。</summary>
    private static ForegroundApp? FindRealUwpChild(IntPtr parent)
    {
        ForegroundApp? found = null;

        Native.EnumChildWindows(parent, (h, _) =>
        {
            uint pid = PidOf(h);
            if (pid == 0) return true;

            var (name, path) = ProcessInfo(pid);
            if (name.Length == 0) return true;
            if (name.Equals(FrameHost, StringComparison.OrdinalIgnoreCase)) return true;

            string title = WindowTitle(h);
            if (title.Length == 0) return true;

            found = new ForegroundApp(name, path, title, parent);
            return false; // 找到就停
        }, IntPtr.Zero);

        return found;
    }

    private static uint PidOf(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    /// <summary>
    /// 用 QueryFullProcessImageName 取名：对提权进程也能拿到路径（PROCESS_QUERY_LIMITED_INFORMATION
    /// 是 Vista 起专门为这种场景设计的低权限查询）。失败时退回 System.Diagnostics。
    /// </summary>
    private static (string Name, string Path) ProcessInfo(uint pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return Fallback(pid);

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, buffer, ref size)) return Fallback(pid);

            string full = buffer.ToString(0, (int)size);
            return (Path.GetFileName(full), full);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static (string Name, string Path) Fallback(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return (p.ProcessName + ".exe", "");
        }
        catch
        {
            return ("", "");
        }
    }

    internal static string WindowTitle(IntPtr hwnd)
    {
        int len = Native.GetWindowTextLength(hwnd);
        if (len <= 0) return "";

        var sb = new StringBuilder(len + 1);
        int written = Native.GetWindowText(hwnd, sb, sb.Capacity);
        return written > 0 ? sb.ToString(0, written).Trim() : "";
    }

    /// <summary>距最后一次键鼠输入过去了多久。</summary>
    public static TimeSpan IdleTime()
    {
        var info = new Native.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>()
        };

        if (!Native.GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // dwTime 是 32 位 tick，49.7 天回绕一次；用无符号减法得到正确的模差。
        uint idle = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idle);
    }

    /// <summary>屏幕是否已锁定（锁屏时输入桌面切换到了 Winlogon 的安全桌面）。</summary>
    public static bool IsLocked()
    {
        var desktop = Native.OpenInputDesktop(0, false, Native.DESKTOP_SWITCHDESKTOP);
        if (desktop == IntPtr.Zero) return true;
        Native.CloseDesktop(desktop);
        return false;
    }
}
