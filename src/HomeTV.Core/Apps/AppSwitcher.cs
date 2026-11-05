using HomeTV.Core.Platform;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging;

namespace HomeTV.Core.Apps;

/// <param name="MaxBackgroundApps">Apps kept open out of sight before the least recently used one is closed.</param>
public sealed record AppSwitcherOptions(int MaxBackgroundApps = 2);

/// <summary>
/// Decides which apps are open, which one is in front, and which are tiled in split screen. Leaving an app keeps it
/// open in the background (pausing its video unless it's <see cref="AppDefinition.PlayInBackground"/>), so going
/// back to it is instant; past <see cref="AppSwitcherOptions.MaxBackgroundApps"/> the oldest one is closed.
/// </summary>
public sealed class AppSwitcher(IAppHost host, AppRegistry apps, SessionState session, AppSwitcherOptions options, ILogger<AppSwitcher> logger)
{
    public const int MinTiles = 2;
    public const int MaxTiles = 4;

    /// <summary>How long split screen waits for an app it had to start (Steam may have to start first).</summary>
    private static readonly TimeSpan TileWindowTimeout = TimeSpan.FromSeconds(30);

    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Open apps, most recently used first.</summary>
    private readonly List<string> _running = [];

    private string? _foreground;
    private List<string>? _split;

    /// <summary>Puts <paramref name="app"/> in front, starting it if it isn't open. In split screen, a tile's app just gets focus.</summary>
    /// <param name="url">
    /// Open this page (a "Continue watching" entry). If the app is already open, its tab goes to the page, or is just
    /// brought back when <paramref name="isSamePage"/> says it's already there; only if its page can't be reached is
    /// the app restarted at the page.
    /// </param>
    /// <param name="isSamePage">Whether the address an open app is showing is already <paramref name="url"/>'s video.</param>
    public Task LaunchAsync(AppDefinition app, string? url = null, CancellationToken ct = default, Func<string, bool>? isSamePage = null) =>
        LockedAsync(async () =>
        {
            if (url is null && _split is not null && _split.Contains(app.Id, Ids))
                await FocusTileCoreAsync(app.Id, ct);
            else
                await ShowFullCoreAsync(app, url, isSamePage, ct);
        }, ct);

    /// <summary>Back to the launcher; open apps stay open behind it.</summary>
    public Task HomeAsync(CancellationToken ct = default) =>
        LockedAsync(async () =>
        {
            await LeaveAsync(keep: [], ct);
            _foreground = null;
            await host.ShowLauncherAsync(ct);
        }, ct);

    public Task CloseAsync(string appId, CancellationToken ct = default) =>
        LockedAsync(async () =>
        {
            if (Find(appId) is not { } id)
                return;
            await host.CloseAsync(id, ct);
            await ForgetCoreAsync(id, ct);
        }, ct);

    /// <summary>
    /// Tiles 2–4 apps (starting any that aren't open). Fewer than two ends split screen, with the given app (or the
    /// active tile) full screen.
    /// </summary>
    public Task SplitAsync(IReadOnlyList<string> appIds, CancellationToken ct = default) =>
        LockedAsync(async () =>
        {
            var tiles = appIds.Select(apps.Find).OfType<AppDefinition>().DistinctBy(a => a.Id, Ids).ToList();
            if (tiles.Count > MaxTiles)
            {
                logger.LogWarning("Split screen asked for {Count} apps; showing the first {Max}", tiles.Count, MaxTiles);
                tiles = tiles[..MaxTiles];
            }

            if (tiles.Count < MinTiles)
            {
                var full = tiles.FirstOrDefault() ?? (_foreground is { } id ? apps.Find(id) : null);
                if (full is not null)
                    await ShowFullCoreAsync(full, null, null, ct);
                return;
            }

            await LeaveAsync(keep: tiles.Select(a => a.Id), ct);

            // One at a time: a window that appears while two Edge apps are starting could belong to either.
            foreach (var app in tiles.Where(a => Find(a.Id) is null))
            {
                await host.LaunchAsync(app, null, ct);
                Touch(app.Id);
                if (!await host.WaitForWindowAsync(app.Id, TileWindowTimeout, ct))
                {
                    logger.LogWarning("{App} didn't open in time for split screen", app.Name);
                    Remove(app.Id);
                }
            }

            var ids = tiles.Select(a => a.Id).Where(id => Find(id) is not null).ToList();
            if (ids.Count < MinTiles)
            {
                if (ids.Count == 1)
                    await ShowFullCoreAsync(apps.Find(ids[0])!, null, null, ct);
                else
                {
                    _foreground = null;
                    await host.ShowLauncherAsync(ct);
                }
                return;
            }

            var active = _foreground is { } current && ids.Contains(current, Ids) ? current : ids[0];
            _split = ids;
            _foreground = active;
            foreach (var id in ids.AsEnumerable().Reverse())
                Touch(id);
            Touch(active);
            await host.ArrangeAsync(ids, active, ct);
            await TrimAsync(ct);
        }, ct);

    /// <summary>Opens an Edge app's sign-in page (see <see cref="IAppHost.SignInAsync"/>) in front.</summary>
    public Task SignInAsync(AppDefinition app, CancellationToken ct = default) =>
        LockedAsync(async () =>
        {
            if (app.SignInUrl is null || app.Untrusted)
                return;
            await LeaveAsync(keep: [], ct);
            Remove(app.Id); // The host closes it first.
            await host.SignInAsync(app, ct);
            Touch(app.Id);
            _foreground = app.Id;
            await TrimAsync(ct);
        }, ct);

    /// <summary>
    /// An app HomeTV didn't launch appeared in front (a Steam game started from Big Picture). It joins the open apps,
    /// so it can be switched back to, tiled and closed from the phone.
    /// </summary>
    public Task OnAppDiscoveredAsync(AppDefinition app) =>
        LockedAsync(async () =>
        {
            apps.AddDiscovered(app);
            await LeaveAsync(keep: [app.Id], CancellationToken.None);
            Touch(app.Id);
            _foreground = app.Id;
            await TrimAsync(CancellationToken.None);
        }, CancellationToken.None);

    /// <summary>An app's window went away on its own.</summary>
    public Task OnAppExitedAsync(string appId) =>
        LockedAsync(async () =>
        {
            if (Find(appId) is { } id)
                await ForgetCoreAsync(id, CancellationToken.None);
        }, CancellationToken.None);

    /// <summary>The user clicked into a tile: it gets the sound and the remote's keys.</summary>
    public Task OnAppActivatedAsync(string appId) =>
        LockedAsync(async () =>
        {
            if (_split is not null && _split.Contains(appId, Ids) && !Ids.Equals(_foreground, appId))
                await FocusTileCoreAsync(_split.First(id => Ids.Equals(id, appId)), CancellationToken.None);
        }, CancellationToken.None);

    private async Task ShowFullCoreAsync(AppDefinition app, string? url, Func<string, bool>? isSamePage, CancellationToken ct)
    {
        await LeaveAsync(keep: [app.Id], ct);

        var running = Find(app.Id) is not null;
        if (running && url is not null)
        {
            // Reuse the open tab: relaunching an open Edge profile hands the page to the running browser as a second
            // tab, and then the same video plays twice.
            if (await host.CurrentUrlAsync(app.Id, ct) is { } current && isSamePage?.Invoke(current) == true)
                url = null; // Already on it (paused when we went Home): just bring it back.
            else if (await host.NavigateAsync(app.Id, url, ct))
                url = null;
            else
            {
                await host.CloseAsync(app.Id, ct);
                Remove(app.Id);
                running = false;
            }
        }
        if (running && !await host.ShowAsync(app.Id, ct))
        {
            Remove(app.Id); // Its window went away and the exit hasn't been reported yet.
            running = false;
        }
        if (!running)
            await host.LaunchAsync(app, url, ct);

        Touch(app.Id);
        _foreground = app.Id;
        await TrimAsync(ct);
    }

    private async Task FocusTileCoreAsync(string appId, CancellationToken ct)
    {
        _foreground = appId;
        Touch(appId);
        await host.ArrangeAsync(_split!, appId, ct);
    }

    /// <summary>Takes a closed app out of the lists and fills the gap it left on screen.</summary>
    private async Task ForgetCoreAsync(string appId, CancellationToken ct)
    {
        Remove(appId);

        if (_split is not null && _split.Remove(appId))
        {
            if (_split.Count >= MinTiles)
            {
                var active = Ids.Equals(_foreground, appId) ? _split[0] : _foreground!;
                _foreground = active;
                await host.ArrangeAsync(_split, active, ct);
            }
            else
            {
                var last = _split[0];
                _split = null;
                _foreground = last;
                if (!await host.ShowAsync(last, ct))
                {
                    Remove(last);
                    _foreground = null;
                    await host.ShowLauncherAsync(ct);
                }
            }
        }
        else if (Ids.Equals(_foreground, appId))
        {
            _foreground = null;
            await host.ShowLauncherAsync(ct);
        }
    }

    /// <summary>Pauses what's on screen (except <paramref name="keep"/>) as it goes into the background, and ends split screen.</summary>
    private async Task LeaveAsync(IEnumerable<string> keep, CancellationToken ct)
    {
        var onScreen = _split ?? (_foreground is null ? [] : [_foreground]);
        var staying = keep.ToHashSet(Ids);
        foreach (var id in onScreen.Where(id => !staying.Contains(id)).ToList())
        {
            if (apps.Find(id) is { PlayInBackground: false })
                await host.PauseAsync(id, ct);
        }
        _split = null;
    }

    /// <summary>Closes the least recently used background apps beyond the limit.</summary>
    private async Task TrimAsync(CancellationToken ct)
    {
        var background = _running.Where(id => !Ids.Equals(id, _foreground) && _split?.Contains(id, Ids) != true).ToList();
        foreach (var id in background.Skip(Math.Max(0, options.MaxBackgroundApps)).ToList())
        {
            logger.LogInformation("Closing {App}: more than {Max} apps open in the background", id, options.MaxBackgroundApps);
            await host.CloseAsync(id, ct);
            Remove(id);
        }
    }

    private string? Find(string appId) => _running.FirstOrDefault(id => Ids.Equals(id, appId));

    private void Touch(string appId)
    {
        _running.RemoveAll(id => Ids.Equals(id, appId));
        _running.Insert(0, appId);
    }

    private void Remove(string appId)
    {
        _running.RemoveAll(id => Ids.Equals(id, appId));
        apps.RemoveDiscovered(appId); // A closed game is gone for good; tiles are unaffected.
    }

    /// <summary>For platform event handlers, which nobody awaits.</summary>
    public async Task SafelyAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't update the open apps");
        }
    }

    /// <summary>Runs one change at a time, and tells clients the outcome even if the change failed partway.</summary>
    private async Task LockedAsync(Func<Task> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await change();
        }
        finally
        {
            session.SetApps(_foreground, _running, _split);
            _gate.Release();
        }
    }
}
