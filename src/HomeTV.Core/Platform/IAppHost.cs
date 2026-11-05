using HomeTV.Core.Apps;
using HomeTV.Core.Continue;

namespace HomeTV.Core.Platform;

/// <summary>
/// Starts, shows, tiles and closes the apps (Edge windows, Steam, Spotify) that sit on top of the launcher. Several
/// can be open at once; which ones, and which is in front, is <see cref="AppSwitcher"/>'s call.
/// </summary>
public interface IAppHost
{
    /// <summary>
    /// Starts <paramref name="app"/> and puts it in front, full screen, once its window appears. <paramref name="url"/>
    /// opens an Edge app at that page instead of its usual start page. Leaves other running apps alone.
    /// </summary>
    Task LaunchAsync(AppDefinition app, string? url = null, CancellationToken ct = default);

    /// <summary>Waits for a launched app's window to appear. False if it didn't, or the app isn't running.</summary>
    Task<bool> WaitForWindowAsync(string appId, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Puts a running app in front, full screen (undoing any tiling). False if it isn't running any more.</summary>
    Task<bool> ShowAsync(string appId, CancellationToken ct = default);

    /// <summary>Brings the launcher in front of every app. The apps keep running behind it.</summary>
    Task ShowLauncherAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens an Edge app's <see cref="AppDefinition.SignInUrl"/> in a normal Edge window on its profile, without the
    /// DevTools port, after closing the app (and every Edge process on that profile) if it's open. The window is
    /// tracked as the app until it's closed.
    /// </summary>
    Task SignInAsync(AppDefinition app, CancellationToken ct = default);

    /// <summary>
    /// Closes a running app for real: politely first, then by ending its process if it's still there a few seconds
    /// later (Spotify hides in the tray, Edge keeps running in the background). Doesn't raise <see cref="AppExited"/>.
    /// </summary>
    Task CloseAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// The address of the page a running Edge app (with its own profile) is showing, or null if it isn't running
    /// or HomeTV can't see into it.
    /// </summary>
    Task<string?> CurrentUrlAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// Sends a running Edge app's page to <paramref name="url"/> in the same tab. False if it isn't running or
    /// HomeTV can't reach its page (no profile, Edge not answering).
    /// </summary>
    Task<bool> NavigateAsync(string appId, string url, CancellationToken ct = default);

    /// <summary>Pauses any video or audio an app is playing, where the app allows it (Edge apps with a profile).</summary>
    Task PauseAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// Tiles running apps across the screen in <paramref name="appIds"/> order (2–4 of them). <paramref name="active"/>
    /// gets keyboard focus and sound; the others are muted where possible.
    /// </summary>
    Task ArrangeAsync(IReadOnlyList<string> appIds, string active, CancellationToken ct = default);

    /// <summary>
    /// Whether the mouse wheel would scroll anything under the pointer. Null when that can't be told: the pointer
    /// isn't over an app HomeTV opened, or the app isn't one whose page it can look into (an Edge app with a profile).
    /// </summary>
    Task<bool?> CanScrollAtPointerAsync(CancellationToken ct = default);

    /// <summary>Maintenance escape hatch: starts Explorer on top of the HomeTV shell.</summary>
    void OpenDesktop();

    /// <summary>
    /// Raised with the app id when an app goes away on its own (the user quit it, it crashed, or its window never
    /// appeared). Not raised for <see cref="CloseAsync"/>.
    /// </summary>
    event Action<string>? AppExited;

    /// <summary>
    /// Raised when an app HomeTV didn't launch shows up, e.g. a Steam game started from Big Picture. It's running and
    /// in front; the host tracks it from then on (Home, close, split screen, <see cref="AppExited"/>) like a launched app.
    /// </summary>
    event Action<AppDefinition>? AppDiscovered;

    /// <summary>Raised when a tiled app's window takes focus without HomeTV asking (the user clicked into it).</summary>
    event Action<string>? AppActivated;

    /// <summary>
    /// Raised every few seconds with the app id and what its page shows, for Edge apps that have a
    /// <see cref="AppDefinition.ResumePattern"/>. Feeds "Continue watching".
    /// </summary>
    event Action<string, PageSnapshot>? PageObserved;
}
