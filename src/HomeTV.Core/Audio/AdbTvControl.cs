using System.Diagnostics;
using HomeTV.Core.Platform;
using Microsoft.Extensions.Logging;

namespace HomeTV.Core.Audio;

/// <summary>
/// Talks to a Fire TV (or any Android TV) over the network with <c>adb</c>. The TV needs Developer options →
/// ADB debugging on, and the first connection from the PC has to be allowed on the TV ("Always allow").
/// </summary>
public sealed class AdbTvControl(TvConfig? config, ILogger<AdbTvControl> logger) : ITvControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(config?.Host);

    private string Serial => $"{config!.Host}:{config.Port}";

    public async Task RunAsync(string command, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new TvControlException("The TV's address isn't set up (tv.host in audio.json)");

        await StartServerAsync(ct);

        // Connecting is a no-op ("already connected") when the connection is still up, which it usually is.
        var (_, connect) = await AdbAsync(ct, "connect", Serial);
        if (connect.Contains("failed to authenticate", StringComparison.OrdinalIgnoreCase))
            throw new TvControlException("Allow the PC on the TV (tick Always allow), then try again");
        if (!connect.Contains("connected to", StringComparison.OrdinalIgnoreCase))
            throw new TvControlException($"Couldn't reach the TV at {Serial}");

        var (exitCode, output) = await AdbAsync(ct, "-s", Serial, "shell", command);
        if (output.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            throw new TvControlException("Allow the PC on the TV (tick Always allow), then try again");
        if (exitCode != 0)
        {
            logger.LogWarning("TV command {Command} exited with {ExitCode}: {Output}", command, exitCode, output);
            throw new TvControlException("The TV didn't accept the command");
        }
        logger.LogInformation("TV: {Command}", command);
    }

    /// <summary>
    /// The first adb command starts adb's background server, which inherits that command's output pipes and keeps
    /// them open, so reading its output would never finish. Starting the server on its own, without pipes, avoids that.
    /// </summary>
    private async Task StartServerAsync(CancellationToken ct)
    {
        if (_serverStarted)
            return;
        var (exitCode, _) = await AdbAsync(ct, redirect: false, "start-server");
        _serverStarted = exitCode == 0;
    }

    private bool _serverStarted;

    private Task<(int ExitCode, string Output)> AdbAsync(CancellationToken ct, params string[] args) => AdbAsync(ct, redirect: true, args);

    private async Task<(int ExitCode, string Output)> AdbAsync(CancellationToken ct, bool redirect, params string[] args)
    {
        var start = new ProcessStartInfo(config!.Adb)
        {
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new TvControlException("Couldn't start adb");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new TvControlException("adb isn't installed (winget install Google.PlatformTools)");
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(Timeout);
            var stdout = redirect ? process.StandardOutput.ReadToEndAsync(timeout.Token) : Task.FromResult("");
            var stderr = redirect ? process.StandardError.ReadToEndAsync(timeout.Token) : Task.FromResult("");
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, (await stdout + await stderr).Trim());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Kill only adb itself: its tree includes the background server when this call started it.
                try { process.Kill(); } catch (InvalidOperationException) { }
                _serverStarted = false;
                throw new TvControlException("The TV didn't answer (is it on?)");
            }
        }
    }
}
