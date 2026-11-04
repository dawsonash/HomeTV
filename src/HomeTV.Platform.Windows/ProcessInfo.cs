using System.Diagnostics;
using System.Runtime.InteropServices;
using static HomeTV.Platform.Windows.NativeMethods;

namespace HomeTV.Platform.Windows;

/// <summary>What a process is (its exe) and how it was started (its command line), and ending it for good.</summary>
internal static class ProcessInfo
{
    /// <summary>The full path of the process's exe, or null if it's gone or out of reach (elevated, protected).</summary>
    public static string? ImagePath(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>The process's command line, or null if it's gone or out of reach. Same-user processes are readable.</summary>
    public static string? CommandLine(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
                return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
                    return null;
                // A UNICODE_STRING (byte length, max length, pointer) whose text follows it in the same buffer.
                var length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Ends a process and its children. False if it was already gone.</summary>
    public static bool Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The Edge processes started with this <c>--user-data-dir</c>: one HomeTV profile's whole browser (its main process,
    /// renderers, GPU and utility processes all carry the flag).
    /// </summary>
    public static List<int> EdgeProcessesFor(string userDataDir)
    {
        var needle = "--user-data-dir=" + userDataDir.TrimEnd('\\');
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("msedge"))
        {
            using (process)
            {
                if (CommandLine(process.Id) is { } line && line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    ids.Add(process.Id);
            }
        }
        return ids;
    }
}
