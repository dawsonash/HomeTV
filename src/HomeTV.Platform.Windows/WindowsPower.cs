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
}
