using System.Diagnostics;
using HomeTV.Core.Apps;
using HomeTV.Core.Platform;
using Microsoft.Extensions.Logging;
using static HomeTV.Platform.Windows.NativeMethods;

namespace HomeTV.Platform.Windows;

/// <summary>
/// Launches apps and tracks the window each one opens, so Home can close it and the launcher comes back when the
/// user quits the app some other way. One app at a time: launching another closes the current one.
/// </summary>
/// <remarks>
/// Tracking is by window, not process: Edge hands an --app launch to an already-running Edge (so the process we
/// start exits at once), and Steam Big Picture lives in steamwebhelper, not the process the URI starts.
/// </remarks>
public sealed class WindowsAppHost(LauncherWindow launcher, ILogger<WindowsAppHost> logger) : IAppHost, IDisposable
{
    /// <summary>Steam may have to start (and update) first, so give windows a while to appear.</summary>
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Polls the window must be gone for before it counts as closed (rides out full-screen transitions).</summary>
    private const int MissesBeforeExit = 3;

    private static readonly string EdgePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");

    private static readonly string ProfilesRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTV", "edge-profiles");

    private readonly Lock _lock = new();
    private RunningApp? _current;

    public event Action<string>? AppExited;

    public Task LaunchAsync(AppDefinition app, CancellationToken ct = default)
    {
        CloseCurrent();

        var windowProcess = WindowProcess(app);
        // Snapshot first, so a window that was already open (e.g. the user's own Edge) isn't mistaken for the app's.
        var existing = windowProcess is null ? [] : TopLevelWindows.List(windowProcess).Select(w => w.Handle).ToHashSet();

        var startInfo = app.Kind switch
        {
            AppKind.Edge => EdgeStartInfo(app),
            AppKind.Shell => new ProcessStartInfo(app.Target) { UseShellExecute = true },
            _ => throw new ArgumentOutOfRangeException(nameof(app), app.Kind, null),
        };
        logger.LogInformation("Launching {App}: {File} {Args}", app.Name, startInfo.FileName, string.Join(' ', startInfo.ArgumentList));
        Process.Start(startInfo)?.Dispose();

        if (windowProcess is null)
        {
            logger.LogWarning("{App} has no windowProcess in apps.json, so Home can't close it", app.Name);
            return Task.CompletedTask;
        }

        var running = new RunningApp(app, windowProcess);
        lock (_lock)
            _current = running;
        _ = Task.Run(() => WatchAsync(running, existing));
        return Task.CompletedTask;
    }

    public Task ReturnHomeAsync(CancellationToken ct = default)
    {
        CloseCurrent();
        launcher.BringToFront();
        return Task.CompletedTask;
    }

    public void OpenDesktop() => Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();

    public void Dispose()
    {
        lock (_lock)
        {
            _current?.Stop.Cancel();
            _current = null;
        }
    }

    private async Task WatchAsync(RunningApp running, HashSet<IntPtr> existing)
    {
        var ct = running.Stop.Token;
        try
        {
            var deadline = DateTime.UtcNow + WindowTimeout;
            while (running.Window == IntPtr.Zero)
            {
                if (DateTime.UtcNow > deadline)
                {
                    logger.LogWarning("{App}'s window didn't appear within {Seconds}s; back to the launcher", running.App.Name, WindowTimeout.TotalSeconds);
                    OnExited(running);
                    return;
                }
                await Task.Delay(PollInterval, ct);
                running.Window = FindWindow(running, existing);
            }

            logger.LogInformation("Tracking {App} window 0x{Handle:X} \"{Title}\"", running.App.Name, running.Window, TopLevelWindows.Title(running.Window));
            TopLevelWindows.BringToFront(running.Window);

            var misses = 0;
            while (misses < MissesBeforeExit)
            {
                await Task.Delay(PollInterval, ct);
                misses = TopLevelWindows.IsOpen(running.Window) ? 0 : misses + 1;
            }

            logger.LogInformation("{App} closed", running.App.Name);
            OnExited(running);
        }
        catch (OperationCanceledException)
        {
            // Home was pressed or another app launched; whoever cancelled handles the launcher.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lost track of {App}", running.App.Name);
            OnExited(running);
        }
    }

    private static IntPtr FindWindow(RunningApp running, HashSet<IntPtr> existing)
    {
        var candidates = TopLevelWindows.List(running.WindowProcess);
        if (running.App.WindowTitle is { } title)
            candidates = candidates.Where(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)).ToList();

        // A new window is always best. With a title to match, an already-open one (Big Picture left open) is fine too.
        var match = candidates.FirstOrDefault(w => !existing.Contains(w.Handle));
        if (match.Handle == IntPtr.Zero && running.App.WindowTitle is not null)
            match = candidates.FirstOrDefault();
        return match.Handle;
    }

    private void OnExited(RunningApp running)
    {
        lock (_lock)
        {
            if (_current != running)
                return;
            _current = null;
        }
        launcher.BringToFront();
        AppExited?.Invoke(running.App.Id);
    }

    private void CloseCurrent()
    {
        RunningApp? running;
        lock (_lock)
        {
            running = _current;
            _current = null;
        }
        if (running is null)
            return;

        running.Stop.Cancel();

        if (running.App.CloseTarget is { } closeTarget)
        {
            logger.LogInformation("Closing {App} via {Target}", running.App.Name, closeTarget);
            Process.Start(new ProcessStartInfo(closeTarget) { UseShellExecute = true })?.Dispose();
            return;
        }

        // Home pressed while the app is still starting: look once more before giving up.
        var window = running.Window != IntPtr.Zero ? running.Window : FindWindow(running, []);
        if (window == IntPtr.Zero || !TopLevelWindows.IsOpen(window))
        {
            logger.LogWarning("Couldn't find {App}'s window to close", running.App.Name);
            return;
        }

        // WM_CLOSE, like clicking X: Edge saves cookies and session state, unlike killing the process.
        logger.LogInformation("Closing {App} window 0x{Handle:X}", running.App.Name, window);
        PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static string? WindowProcess(AppDefinition app) =>
        app.WindowProcess ?? (app.Kind == AppKind.Edge ? "msedge" : null);

    private static ProcessStartInfo EdgeStartInfo(AppDefinition app)
    {
        var startInfo = new ProcessStartInfo(EdgePath);
        startInfo.ArgumentList.Add($"--app={app.Target}");
        startInfo.ArgumentList.Add("--start-fullscreen");
        startInfo.ArgumentList.Add("--no-first-run");
        if (app.EdgeProfile is not null)
            startInfo.ArgumentList.Add($"--user-data-dir={Path.Combine(ProfilesRoot, app.EdgeProfile)}");
        if (app.UserAgent is not null)
            startInfo.ArgumentList.Add($"--user-agent={app.UserAgent}");
        return startInfo;
    }

    private sealed class RunningApp(AppDefinition app, string windowProcess)
    {
        public AppDefinition App { get; } = app;
        public string WindowProcess { get; } = windowProcess;
        public CancellationTokenSource Stop { get; } = new();
        public IntPtr Window { get; set; }
    }
}
