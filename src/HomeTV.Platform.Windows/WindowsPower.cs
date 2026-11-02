using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeTV.Core.Platform;
using Microsoft.Extensions.Logging;

namespace HomeTV.Platform.Windows;

public sealed class WindowsPower(ILogger<WindowsPower> logger) : IPower
{
    public void Sleep()
    {
        // With hibernate disabled (setup-box.ps1 runs `powercfg /hibernate off`) this enters S3/standby.
        if (!NativeMethods.SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
            logger.LogError("SetSuspendState failed (error {Error})", Marshal.GetLastWin32Error());
    }

    // Standard users (the tv account) hold the shutdown privilege on client Windows, so no elevation is needed.
    public void Restart()
    {
        logger.LogWarning("Restarting the PC");
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }
}
