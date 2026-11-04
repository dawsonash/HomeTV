using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeTV.Core.Apps;
using HomeTV.Core.Continue;
using HomeTV.Core.Platform;
using Microsoft.Extensions.Logging;
using static HomeTV.Platform.Windows.NativeMethods;

namespace HomeTV.Platform.Windows;

/// <summary>
/// Launches apps and tracks the window each one opens, so they can be brought back, tiled and closed, and so the
/// switcher hears when one goes away by itself. Several apps can be open; the ones out of sight sit behind the launcher.
/// </summary>
/// <remarks>
/// Tracking is by window, not process: Edge hands an --app launch to an already-running Edge (so the process we
/// start exits at once), and Steam Big Picture lives in steamwebhelper, not the process the URI starts.
/// Edge apps with their own profile are moved and tiled through DevTools (<see cref="EdgeDevTools"/>), which knows
/// about full screen and the window chrome; anything else is moved with plain Win32 calls.
/// </remarks>
public sealed class WindowsAppHost : IAppHost, IDisposable
{
    private readonly LauncherWindow launcher;
    private readonly ILogger<WindowsAppHost> logger;

    public WindowsAppHost(LauncherWindow launcher, ILogger<WindowsAppHost> logger)
    {
        this.launcher = launcher;
        this.logger = logger;
        _ = Task.Run(WatchSteamAsync);
    }

    /// <summary>How long a closed app gets to quit by itself before its process is ended.</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(8);

    /// <summary>How often to look for a Steam game that started.</summary>
    private static readonly TimeSpan SteamPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>A game HomeTV closed isn't picked up again while it's still on its way out.</summary>
    private static readonly TimeSpan ClosedGameQuietPeriod = TimeSpan.FromSeconds(20);

    private readonly CancellationTokenSource _disposed = new();
    private readonly Dictionary<string, DateTime> _closedGames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Steam may have to start (and update) first, so give windows a while to appear.</summary>
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Polls the window must be gone for before it counts as closed (rides out full-screen transitions).</summary>
    private const int MissesBeforeExit = 3;

    /// <summary>How often a tracked Edge app's page is looked at for "Continue watching", in <see cref="PollInterval"/>s.</summary>
    private const int PollsPerPageLook = 10;

    private static readonly string EdgePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");

    private static readonly string ProfilesRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTV", "edge-profiles");

    private readonly Lock _lock = new();
    private readonly Dictionary<string, RunningApp> _apps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app that should be in front once its window shows up; null for the launcher (or split screen).</summary>
    private string? _front;

    /// <summary>The tile with focus and sound, while tiled.</summary>
    private string? _activeTile;

    /// <summary>The tiles' app ids in screen order, to tell a change of focus from a change of layout.</summary>
    private string? _layout;

    public event Action<string>? AppExited;

    public event Action<string>? AppActivated;

    public event Action<string, PageSnapshot>? PageObserved;

    public event Action<AppDefinition>? AppDiscovered;

    public Task LaunchAsync(AppDefinition app, string? url = null, CancellationToken ct = default)
    {
        var windowProcess = WindowProcess(app);
        // Snapshot first, so a window that was already open (the user's own Edge, another app) isn't mistaken for this one.
        var existing = windowProcess is null ? [] : TopLevelWindows.List(windowProcess).Select(w => w.Handle).ToHashSet();

        var startInfo = app.Kind switch
        {
            AppKind.Edge => EdgeStartInfo(app, url),
            AppKind.Shell => new ProcessStartInfo(app.Target) { UseShellExecute = true },
            _ => throw new ArgumentOutOfRangeException(nameof(app), app.Kind, null),
        };
        logger.LogInformation("Launching {App}: {File} {Args}", app.Name, startInfo.FileName, string.Join(' ', startInfo.ArgumentList));
        Process.Start(startInfo)?.Dispose();

        if (windowProcess is null)
        {
            logger.LogWarning("{App} has no windowProcess in apps.json, so HomeTV can't bring it back or close it", app.Name);
            return Task.CompletedTask;
        }

        Track(app, windowProcess, existing);
        return Task.CompletedTask;
    }

    public async Task SignInAsync(AppDefinition app, CancellationToken ct = default)
    {
        if (ProfileDir(app) is not { } profileDir || app.SignInUrl is not { } url)
            return;

        // The DevTools port is opened by the profile's browser process, and a new window joins the running one,
        // so Edge must be fully closed for the sign-in window to come up without it.
        if (Get(app.Id) is not null)
            await CloseAsync(app.Id, ct);
        else if (EdgeDevTools.ReadPort(profileDir) is { } port)
            await EdgeDevTools.CloseBrowserAsync(port, ct);
        await EndEdgeProfileAsync(app, profileDir, ct);
        try
        {
            // Edge rewrites it when it opens the port; left over, it would point HomeTV at a port nobody listens on.
            File.Delete(Path.Combine(profileDir, "DevToolsActivePort"));
        }
        catch (IOException)
        {
        }

        var existing = TopLevelWindows.List("msedge").Select(w => w.Handle).ToHashSet();
        var startInfo = new ProcessStartInfo(EdgePath);
        startInfo.ArgumentList.Add($"--user-data-dir={profileDir}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--start-maximized");
        startInfo.ArgumentList.Add("--force-renderer-accessibility=basic"); // So the phone's keyboard comes up.
        startInfo.ArgumentList.Add(url);
        logger.LogInformation("Opening {App} sign-in: {Url}", app.Name, url);
        Process.Start(startInfo)?.Dispose();
        Track(app, "msedge", existing);
    }

    /// <summary>Starts watching for a just-launched app's window, and remembers it as the one in front.</summary>
    private void Track(AppDefinition app, string windowProcess, HashSet<IntPtr> existing)
    {
        var running = new RunningApp(app, windowProcess);
        RunningApp? replaced;
        lock (_lock)
        {
            _apps.TryGetValue(app.Id, out replaced);
            _apps[app.Id] = running;
            _front = app.Id;
        }
        replaced?.Stop.Cancel();
        _ = Task.Run(() => WatchAsync(running, existing, adopted: false));
    }

    public async Task<bool> WaitForWindowAsync(string appId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Get(appId) is not { } running)
                return false;
            if (running.Window != IntPtr.Zero)
                return true;
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(250, ct);
        }
    }

    public async Task<bool> ShowAsync(string appId, CancellationToken ct = default)
    {
        if (Get(appId) is not { } running)
            return false;
        lock (_lock)
        {
            _front = running.App.Id;
            _activeTile = null;
            _layout = null;
        }
        if (running.Window == IntPtr.Zero)
            return true; // Still starting: the watcher brings it in front when it appears.
        if (!TopLevelWindows.IsOpen(running.Window))
            return false;

        if (running.Tiled)
            await UntileAsync(running, ct);
        TopLevelWindows.BringToFront(running.Window);
        return true;
    }

    public Task ShowLauncherAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _front = null;
            _activeTile = null;
            _layout = null;
        }
        launcher.BringToFront();
        return Task.CompletedTask;
    }

    public async Task CloseAsync(string appId, CancellationToken ct = default)
    {
        RunningApp? running;
        lock (_lock)
        {
            if (!_apps.Remove(appId, out running))
                return;
            if (string.Equals(_front, appId, StringComparison.OrdinalIgnoreCase))
                _front = null;
            if (running.App.Discovered)
                _closedGames[appId] = DateTime.UtcNow;
        }
        running.Stop.Cancel();
        var app = running.App;

        if (app.CloseTarget is { } closeTarget)
        {
            logger.LogInformation("Closing {App} via {Target}", app.Name, closeTarget);
            Process.Start(new ProcessStartInfo(closeTarget) { UseShellExecute = true })?.Dispose();
            return;
        }

        // Closed while still starting: look once more.
        var window = running.Window != IntPtr.Zero ? running.Window : FindWindow(running, []);
        var hasWindow = window != IntPtr.Zero && TopLevelWindows.IsOpen(window);

        if (ProfileDir(app) is { } profileDir)
        {
            // The app has the profile to itself, so quit its whole browser: closing just the window would leave Edge
            // running in the background. Browser.close saves cookies and the session, like quitting Edge does.
            var closed = EdgeDevTools.ReadPort(profileDir) is { } port && await EdgeDevTools.CloseBrowserAsync(port, ct);
            if (!closed && hasWindow)
                PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            logger.LogInformation("Closing {App} (Edge profile {Profile})", app.Name, app.EdgeProfile);
            _ = Task.Run(() => EndEdgeProfileAsync(app, profileDir, CancellationToken.None));
            return;
        }

        if (!hasWindow)
        {
            logger.LogWarning("Couldn't find {App}'s window to close", app.Name);
            return;
        }

        // WM_CLOSE, like clicking X, so the app can save. Edge apps without a profile share the user's own Edge, so
        // that's all they get; anything else is ended if it's still running after a few seconds (Spotify only hides
        // in the tray, and games may ask "are you sure?").
        GetWindowThreadProcessId(window, out var pid);
        logger.LogInformation("Closing {App} window 0x{Handle:X}", app.Name, window);
        PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (app.Kind != AppKind.Edge && pid != 0)
            _ = Task.Run(() => EndProcessAsync(app, (int)pid));
    }

    /// <summary>Waits for a closed app's process to exit, and ends it (and its children) if it doesn't.</summary>
    private async Task EndProcessAsync(AppDefinition app, int processId)
    {
        try
        {
            var deadline = DateTime.UtcNow + CloseGrace;
            while (ProcessInfo.IsRunning(processId) && DateTime.UtcNow < deadline)
                await Task.Delay(250);
            // Opened again meanwhile (Spotify hands a new launch to the process that's still running): leave it be.
            if (Get(app.Id) is not null)
                return;
            if (ProcessInfo.IsRunning(processId) && ProcessInfo.Kill(processId))
                logger.LogInformation("{App} was still running after it was closed; ended it", app.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't make sure {App} closed", app.Name);
        }
    }

    /// <summary>Waits for every Edge process on an app's profile to exit, and ends any that don't.</summary>
    private async Task EndEdgeProfileAsync(AppDefinition app, string profileDir, CancellationToken ct)
    {
        try
        {
            var deadline = DateTime.UtcNow + CloseGrace;
            List<int> left;
            while ((left = ProcessInfo.EdgeProcessesFor(profileDir)).Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(250, ct);
            // Opened again meanwhile: those processes are the new one's.
            if (left.Count == 0 || Get(app.Id) is not null)
                return;
            logger.LogInformation("{App}: {Count} Edge processes still running after closing; ending them", app.Name, left.Count);
            foreach (var id in left)
                ProcessInfo.Kill(id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't make sure {App}'s Edge closed", app.Name);
        }
    }

    public async Task PauseAsync(string appId, CancellationToken ct = default)
    {
        if (Get(appId) is { } running && DevToolsPort(running.App) is { } port)
            await EdgeDevTools.PauseAsync(port, ct);
    }

    public async Task ArrangeAsync(IReadOnlyList<string> appIds, string active, CancellationToken ct = default)
    {
        var tiles = appIds.Select(Get).OfType<RunningApp>().Where(r => r.Window != IntPtr.Zero).ToList();
        if (tiles.Count == 0)
            return;
        var rects = Layout(tiles.Count);
        var layout = string.Join(',', tiles.Select(t => t.App.Id));
        bool moved;
        lock (_lock)
        {
            moved = !string.Equals(_layout, layout, StringComparison.OrdinalIgnoreCase);
            _layout = layout;
            _front = null;
            _activeTile = active;
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var isActive = string.Equals(tile.App.Id, active, StringComparison.OrdinalIgnoreCase);
            if (moved || !tile.Tiled)
                await PlaceAsync(tile, rects[i], ct);
            if (DevToolsPort(tile.App) is { } port)
                await EdgeDevTools.SetTileAsync(port, muted: !isActive, fit: tile.Tiled ? null : true, ct);
            tile.Tiled = true;
        }

        // Every tile above the launcher, the active one with focus, and then the top row above the bottom row:
        // a bottom tile's title bar sits just above it, over the bottom edge of the tile above, and must stay under it.
        foreach (var (tile, _) in tiles.Zip(rects).OrderBy(p => p.Second.Y == 0))
            SetWindowPos(tile.Window, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (tiles.FirstOrDefault(t => string.Equals(t.App.Id, active, StringComparison.OrdinalIgnoreCase)) is { } focused)
            TopLevelWindows.BringToFront(focused.Window);
        foreach (var (tile, _) in tiles.Zip(rects).Where(p => p.Second.Y == 0))
            SetWindowPos(tile.Window, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>A page's answer stays good this long while the pointer stays put (the page may load more, or end).</summary>
    private static readonly TimeSpan ScrollAnswerLifetime = TimeSpan.FromSeconds(3);

    private (IntPtr Window, POINT Point, long At, bool? Answer) _scrollAnswer;

    public async Task<bool?> CanScrollAtPointerAsync(CancellationToken ct = default)
    {
        if (!GetPhysicalCursorPos(out var point))
            return null;
        var window = GetAncestor(WindowFromPhysicalPoint(point), GA_ROOT);

        var cached = _scrollAnswer;
        if (cached.Window == window && cached.Point.X == point.X && cached.Point.Y == point.Y
            && Environment.TickCount64 - cached.At < ScrollAnswerLifetime.TotalMilliseconds)
            return cached.Answer;

        RunningApp? app;
        lock (_lock)
            app = _apps.Values.FirstOrDefault(r => r.Window == window);
        bool? answer = app is not null && DevToolsPort(app.App) is { } port
            ? await EdgeDevTools.CanScrollAtAsync(port, point.X, point.Y, ct)
            : null;
        _scrollAnswer = (window, point, Environment.TickCount64, answer);
        return answer;
    }

    public void OpenDesktop() => Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();

    public void Dispose()
    {
        _disposed.Cancel();
        lock (_lock)
        {
            foreach (var running in _apps.Values)
                running.Stop.Cancel();
            _apps.Clear();
        }
    }

    /// <summary>Tile rectangles as fractions of the screen: two side by side, three as one big and two small, four in a grid.</summary>
    private static List<Fraction> Layout(int count) => count switch
    {
        1 => [new(0, 0, 1, 1)],
        2 => [new(0, 0, 0.5, 1), new(0.5, 0, 0.5, 1)],
        3 => [new(0, 0, 0.5, 1), new(0.5, 0, 0.5, 0.5), new(0.5, 0.5, 0.5, 0.5)],
        _ => [new(0, 0, 0.5, 0.5), new(0.5, 0, 0.5, 0.5), new(0, 0.5, 0.5, 0.5), new(0.5, 0.5, 0.5, 0.5)],
    };

    private async Task PlaceAsync(RunningApp tile, Fraction rect, CancellationToken ct)
    {
        if (DevToolsPort(tile.App) is { } port && await EdgeDevTools.MetricsAsync(port, ct) is { } screen)
        {
            var placement = new EdgeWindowPlacement.Tile(
                rect.X * screen.ScreenWidth, rect.Y * screen.ScreenHeight, rect.Width * screen.ScreenWidth, rect.Height * screen.ScreenHeight);
            if (await EdgeDevTools.PlaceAsync(port, placement, ct))
                return;
            logger.LogWarning("{App}: DevTools didn't move the window; moving it directly", tile.App.Name);
        }

        // Not an Edge app with a profile: remember where it was, then fit its visible frame to the tile.
        if (tile.Saved is null && GetWindowRect(tile.Window, out var before))
            tile.Saved = new SavedPlacement(before, IsZoomed(tile.Window));
        if (IsZoomed(tile.Window) || IsIconic(tile.Window))
            ShowWindow(tile.Window, SW_RESTORE);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(tile.Window, MONITOR_DEFAULTTOPRIMARY), ref info))
            return;
        var monitor = info.rcMonitor;
        var x = monitor.Left + (int)Math.Round(rect.X * monitor.Width);
        var y = monitor.Top + (int)Math.Round(rect.Y * monitor.Height);
        var width = (int)Math.Round(rect.Width * monitor.Width);
        var height = (int)Math.Round(rect.Height * monitor.Height);

        // GetWindowRect includes the invisible resize borders; the extended frame bounds are what you see.
        var (left, top, right, bottom) = (0, 0, 0, 0);
        if (GetWindowRect(tile.Window, out var outer)
            && DwmGetWindowAttribute(tile.Window, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) == 0)
            (left, top, right, bottom) = (visible.Left - outer.Left, visible.Top - outer.Top, outer.Right - visible.Right, outer.Bottom - visible.Bottom);
        SetWindowPos(tile.Window, HWND_TOP, x - left, y - top, width + left + right, height + top + bottom, SWP_NOACTIVATE);
    }

    private async Task UntileAsync(RunningApp running, CancellationToken ct)
    {
        running.Tiled = false;
        if (DevToolsPort(running.App) is { } port)
        {
            await EdgeDevTools.SetTileAsync(port, muted: null, fit: null, ct);
            if (await EdgeDevTools.PlaceAsync(port, new EdgeWindowPlacement.Fullscreen(), ct))
                return;
        }

        if (running.Saved is { } saved)
        {
            running.Saved = null;
            if (saved.Maximized)
                ShowWindow(running.Window, SW_MAXIMIZE);
            else
                SetWindowPos(running.Window, HWND_TOP, saved.Rect.Left, saved.Rect.Top, saved.Rect.Width, saved.Rect.Height, SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Every couple of seconds, looks for a Steam game HomeTV isn't tracking yet (one started from Big Picture), and
    /// adopts it as an app.
    /// </summary>
    private async Task WatchSteamAsync()
    {
        var ct = _disposed.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SteamPollInterval, ct);
                if (SteamGames.Running() is { } game)
                    Adopt(game);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't look for a running Steam game");
                await Task.Delay(SteamPollInterval * 5, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private void Adopt(SteamGame game)
    {
        var app = new AppDefinition
        {
            Id = $"steam-{game.AppId}",
            Name = game.Name,
            Kind = AppKind.Shell,
            Target = $"steam://rungameid/{game.AppId}",
            WindowProcess = game.Window.ProcessName,
            Color = "#66c0f4",
            Discovered = true,
        };
        var running = new RunningApp(app, game.Window.ProcessName) { Window = game.Window.Handle };
        lock (_lock)
        {
            if (_apps.ContainsKey(app.Id) || _apps.Values.Any(r => r.Window == game.Window.Handle))
                return;
            if (_closedGames.TryGetValue(app.Id, out var closedAt) && DateTime.UtcNow - closedAt < ClosedGameQuietPeriod)
                return;
            _closedGames.Remove(app.Id);
            _apps[app.Id] = running;
            // It's in front: Steam put it there.
            _front = app.Id;
            _activeTile = null;
            _layout = null;
        }
        logger.LogInformation("Steam game {Game} ({Id}) is running in window 0x{Handle:X}", game.Name, game.AppId, game.Window.Handle);
        _ = Task.Run(() => WatchAsync(running, [], adopted: true));
        AppDiscovered?.Invoke(app);
    }

    /// <param name="adopted">Found already open (a Steam game), rather than launched: it's already where it should be.</param>
    private async Task WatchAsync(RunningApp running, HashSet<IntPtr> existing, bool adopted)
    {
        var ct = running.Stop.Token;
        try
        {
            var deadline = DateTime.UtcNow + WindowTimeout;
            while (running.Window == IntPtr.Zero)
            {
                if (DateTime.UtcNow > deadline)
                {
                    logger.LogWarning("{App}'s window didn't appear within {Seconds}s", running.App.Name, WindowTimeout.TotalSeconds);
                    OnExited(running);
                    return;
                }
                await Task.Delay(PollInterval, ct);
                running.Window = FindWindow(running, existing);
            }

            logger.LogInformation("Tracking {App} window 0x{Handle:X} \"{Title}\"", running.App.Name, running.Window, TopLevelWindows.Title(running.Window));
            // New windows jump in front by themselves. If something else was picked while this one was starting,
            // put that back: the app in front, or the launcher (split screen arranges its own tiles).
            RunningApp? front;
            bool tiling;
            lock (_lock)
            {
                front = _front is null ? null : _apps.GetValueOrDefault(_front);
                tiling = _layout is not null;
            }
            if (adopted)
            {
                // Already in front, where Steam put it.
            }
            else if (front == running)
                TopLevelWindows.BringToFront(running.Window);
            else if (front is { Window: var other } && other != IntPtr.Zero)
                TopLevelWindows.BringToFront(other);
            else if (front is null && !tiling)
                launcher.BringToFront();

            var misses = 0;
            var polls = 0;
            var resumeDir = running.App.ResumePattern is null ? null : ProfileDir(running.App);
            var untrustedDir = running.App.Untrusted ? ProfileDir(running.App) : null;
            string? appPage = null;
            while (misses < MissesBeforeExit)
            {
                await Task.Delay(PollInterval, ct);
                misses = TopLevelWindows.IsOpen(running.Window) ? 0 : misses + 1;
                if (misses > 0)
                    continue;
                // An untrusted site's pop-ups and pop-unders: closed within half a second of opening.
                if (untrustedDir is not null && EdgeDevTools.ReadPort(untrustedDir) is { } port)
                    appPage = await EdgeDevTools.CloseOtherPagesAsync(port, appPage, ct);
                if (running.Tiled)
                    NoticeActivation(running);
                if (resumeDir is not null && ++polls % PollsPerPageLook == 0)
                    await LookAtPageAsync(running.App, resumeDir, ct);
            }

            logger.LogInformation("{App} closed", running.App.Name);
            OnExited(running);
        }
        catch (OperationCanceledException)
        {
            // Closed by HomeTV, or relaunched; whoever cancelled takes it from here.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lost track of {App}", running.App.Name);
            OnExited(running);
        }
    }

    /// <summary>A tile the user clicked into becomes the active one.</summary>
    private void NoticeActivation(RunningApp running)
    {
        if (GetForegroundWindow() != running.Window)
            return;
        lock (_lock)
        {
            if (_activeTile is null || string.Equals(_activeTile, running.App.Id, StringComparison.OrdinalIgnoreCase))
                return;
            _activeTile = running.App.Id;
        }
        AppActivated?.Invoke(running.App.Id);
    }

    private async Task LookAtPageAsync(AppDefinition app, string profileDir, CancellationToken ct)
    {
        if (EdgeDevTools.ReadPort(profileDir) is not { } port)
        {
            // Edge was already running for this profile without the DevTools port (e.g. from before an update).
            logger.LogDebug("{App}: Edge has no DevTools port open, can't see its page", app.Name);
            return;
        }
        if (await EdgeDevTools.SnapshotAsync(port, ct) is not { } page)
            return;
        try
        {
            PageObserved?.Invoke(app.Id, page);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't record {App}'s page", app.Name);
        }
    }

    private RunningApp? Get(string appId)
    {
        lock (_lock)
            return _apps.GetValueOrDefault(appId);
    }

    /// <summary>The Edge profile folder of an Edge app that has its own profile (and so a DevTools port).</summary>
    private static string? ProfileDir(AppDefinition app) =>
        app is { Kind: AppKind.Edge, EdgeProfile: { } profile } ? Path.Combine(ProfilesRoot, profile) : null;

    private static int? DevToolsPort(AppDefinition app) => ProfileDir(app) is { } dir ? EdgeDevTools.ReadPort(dir) : null;

    private IntPtr FindWindow(RunningApp running, HashSet<IntPtr> existing)
    {
        HashSet<IntPtr> claimed;
        lock (_lock)
            claimed = _apps.Values.Where(r => r != running && r.Window != IntPtr.Zero).Select(r => r.Window).ToHashSet();

        var candidates = TopLevelWindows.List(running.WindowProcess).Where(w => !claimed.Contains(w.Handle)).ToList();
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
            if (!_apps.TryGetValue(running.App.Id, out var current) || current != running)
                return;
            _apps.Remove(running.App.Id);
        }
        AppExited?.Invoke(running.App.Id);
    }

    private static string? WindowProcess(AppDefinition app) =>
        app.WindowProcess ?? (app.Kind == AppKind.Edge ? "msedge" : null);

    private static ProcessStartInfo EdgeStartInfo(AppDefinition app, string? url)
    {
        var startInfo = new ProcessStartInfo(EdgePath);
        startInfo.ArgumentList.Add($"--app={url ?? app.Target}");
        startInfo.ArgumentList.Add("--start-fullscreen");
        startInfo.ArgumentList.Add("--no-first-run");
        // Edge only tells UI Automation about a page's controls (so WindowsTextFocus sees a search box get focus)
        // once something turns its accessibility tree on; listening for focus events doesn't. "basic" is enough for
        // focus and text fields, and lighter than the full tree a screen reader gets.
        startInfo.ArgumentList.Add("--force-renderer-accessibility=basic");
        if (ProfileDir(app) is { } profileDir)
        {
            startInfo.ArgumentList.Add($"--user-data-dir={profileDir}");
            // Port 0: Edge picks a free loopback port and writes it to DevToolsActivePort (see EdgeDevTools).
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
        }
        if (app.UserAgent is not null)
            startInfo.ArgumentList.Add($"--user-agent={app.UserAgent}");
        return startInfo;
    }

    private readonly record struct Fraction(double X, double Y, double Width, double Height);

    private sealed record SavedPlacement(RECT Rect, bool Maximized);

    private sealed class RunningApp(AppDefinition app, string windowProcess)
    {
        public AppDefinition App { get; } = app;
        public string WindowProcess { get; } = windowProcess;
        public CancellationTokenSource Stop { get; } = new();
        public IntPtr Window { get; set; }

        /// <summary>Placed as a tile (and, for Edge, running the tile script).</summary>
        public bool Tiled { get; set; }

        /// <summary>Where a non-Edge window was before it was tiled.</summary>
        public SavedPlacement? Saved { get; set; }
    }
}
