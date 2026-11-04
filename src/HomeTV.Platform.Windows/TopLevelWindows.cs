using System.Diagnostics;
using System.Runtime.InteropServices;
using static HomeTV.Platform.Windows.NativeMethods;

namespace HomeTV.Platform.Windows;

/// <summary>A visible, top-level, Alt+Tab-able window.</summary>
internal readonly record struct TopLevelWindow(IntPtr Handle, int ProcessId, string ProcessName, string Title);

internal static class TopLevelWindows
{
    /// <summary>App windows (visible, unowned, not tool windows) whose process is <paramref name="processName"/>.</summary>
    public static List<TopLevelWindow> List(string processName)
    {
        var pids = Process.GetProcessesByName(processName).Select(p => { using (p) return p.Id; }).ToHashSet();
        var windows = new List<TopLevelWindow>();
        if (pids.Count == 0)
            return windows;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pids.Contains((int)pid) && IsAppWindow(hWnd))
                windows.Add(new TopLevelWindow(hWnd, (int)pid, processName, Title(hWnd)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>Every app window (visible, unowned, not a tool window), whatever its process.</summary>
    public static List<TopLevelWindow> All()
    {
        var windows = new List<TopLevelWindow>();
        EnumWindows((hWnd, _) =>
        {
            if (IsAppWindow(hWnd))
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                windows.Add(new TopLevelWindow(hWnd, (int)pid, "", Title(hWnd)));
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static bool IsOpen(IntPtr hWnd) => IsWindow(hWnd) && IsWindowVisible(hWnd);

    public static string Title(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length == 0)
            return "";
        var buffer = new char[length + 1];
        var copied = GetWindowText(hWnd, buffer, buffer.Length);
        return new string(buffer, 0, copied);
    }

    /// <summary>
    /// Puts <paramref name="hWnd"/> in front with keyboard focus. Windows only lets the foreground process do
    /// this, and HomeTV usually isn't (an app or nothing is), so tap Alt first: input counts as user activity
    /// and unlocks SetForegroundWindow.
    /// </summary>
    public static void BringToFront(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        if (SetForegroundWindow(hWnd))
            return;

        INPUT[] alt =
        [
            new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU } } },
            new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } },
        ];
        SendInput((uint)alt.Length, alt, Marshal.SizeOf<INPUT>());
        SetForegroundWindow(hWnd);
    }

    private static bool IsAppWindow(IntPtr hWnd) =>
        IsWindowVisible(hWnd)
        && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero
        && (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0;
}
