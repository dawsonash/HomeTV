using HomeTV.Core.Apps;
using HomeTV.Core.Continue;
using HomeTV.Core.NowPlaying;
using HomeTV.Core.Platform;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging;

namespace HomeTV.Core.Remote;

/// <summary>Routes remote commands to the launcher page, OS input injection, or app/power control.</summary>
public sealed class CommandDispatcher(
    IInput input,
    IAppHost appHost,
    AppSwitcher switcher,
    IPower power,
    AppRegistry apps,
    SessionState session,
    ContinueService continueWatching,
    NowPlayingService nowPlaying,
    ILogger<CommandDispatcher> logger)
{
    /// <summary>Longest text a single <see cref="TextCommand"/> may type.</summary>
    public const int MaxTextLength = 500;

    private const int MaxMouseDelta = 2000;

    public async Task DispatchAsync(RemoteCommand command, CancellationToken ct = default)
    {
        switch (command)
        {
            case KeyCommand { Key: var key } when session.LauncherInFront && !IsSystemKey(key):
                session.SendKeyToLauncher(key);
                break;
            case KeyCommand { Key: var key }:
                input.PressKey(key);
                break;
            case MouseMoveCommand move:
                input.MoveMouse(Math.Clamp(move.Dx, -MaxMouseDelta, MaxMouseDelta), Math.Clamp(move.Dy, -MaxMouseDelta, MaxMouseDelta));
                break;
            case ClickCommand click:
                input.Click(click.Button);
                break;
            case ScrollCommand scroll:
                input.Scroll(Math.Clamp(scroll.Dy, -MaxMouseDelta, MaxMouseDelta));
                break;
            case TextCommand { Text: var text } when !string.IsNullOrEmpty(text) && text.Length <= MaxTextLength:
                input.TypeText(text);
                break;
            case LaunchCommand { App: var appId, Continue: var entryId }:
                await LaunchAsync(appId, entryId, ct);
                break;
            case SeekCommand { Position: var position } when double.IsFinite(position):
                await nowPlaying.SeekAsync(position, ct);
                break;
            case HomeCommand:
                await switcher.HomeAsync(ct);
                break;
            case CloseCommand { App: var appId }:
                await switcher.CloseAsync(appId, ct);
                break;
            case SplitCommand { Apps: { } appIds }:
                await switcher.SplitAsync(appIds, ct);
                break;
            case SleepCommand:
                power.Sleep();
                break;
            case DesktopCommand:
                appHost.OpenDesktop();
                break;
            default:
                logger.LogWarning("Ignoring invalid command {Command}", command);
                break;
        }
    }

    private async Task LaunchAsync(string appId, string? entryId, CancellationToken ct)
    {
        var app = apps.Find(appId);
        if (app is null)
        {
            logger.LogWarning("Launch requested for unknown app {AppId}", appId);
            return;
        }

        // Only pages HomeTV saw the app showing itself: a remote can't make Edge open an arbitrary address.
        string? url = null;
        Func<string, bool>? isSamePage = null;
        if (entryId is not null)
        {
            if (continueWatching.Find(entryId) is { } entry && string.Equals(entry.AppId, app.Id, StringComparison.OrdinalIgnoreCase))
            {
                url = continueWatching.ResumeUrl(entry);
                isSamePage = current => continueWatching.IsSamePage(entry, current);
            }
            else
                logger.LogWarning("Unknown continue-watching entry {Entry} for {AppId}; opening its start page", entryId, appId);
        }

        await switcher.LaunchAsync(app, url, ct, isSamePage);
    }

    /// <summary>Keys that always go to the OS, even while the launcher is in front.</summary>
    private static bool IsSystemKey(RemoteKey key) =>
        key is RemoteKey.VolumeUp or RemoteKey.VolumeDown or RemoteKey.Mute;
}
