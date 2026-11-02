using HomeTV.Core.Apps;
using HomeTV.Core.Platform;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging;

namespace HomeTV.Core.Remote;

/// <summary>Routes remote commands to the launcher page, OS input injection, or app/power control.</summary>
public sealed class CommandDispatcher(
    IInput input,
    IAppHost appHost,
    IPower power,
    AppRegistry apps,
    SessionState session,
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
            case LaunchCommand { App: var appId }:
                await LaunchAsync(appId, ct);
                break;
            case HomeCommand:
                await appHost.ReturnHomeAsync(ct);
                session.SetForeground(null);
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

    private async Task LaunchAsync(string appId, CancellationToken ct)
    {
        var app = apps.Find(appId);
        if (app is null)
        {
            logger.LogWarning("Launch requested for unknown app {AppId}", appId);
            return;
        }

        await appHost.LaunchAsync(app, ct);
        session.SetForeground(app.Id);
    }

    /// <summary>Keys that always go to the OS, even while the launcher is in front.</summary>
    private static bool IsSystemKey(RemoteKey key) =>
        key is RemoteKey.VolumeUp or RemoteKey.VolumeDown or RemoteKey.Mute;
}
