using System.Text.Json;
using System.Text.Json.Serialization;
using HomeTV.Core.Platform;

namespace HomeTV.Core.Remote;

/// <summary>Client → server messages sent by the phone remote (and the launcher, for <see cref="LaunchCommand"/>).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(KeyCommand), "key")]
[JsonDerivedType(typeof(MouseMoveCommand), "mouseMove")]
[JsonDerivedType(typeof(ClickCommand), "click")]
[JsonDerivedType(typeof(ScrollCommand), "scroll")]
[JsonDerivedType(typeof(TextCommand), "text")]
[JsonDerivedType(typeof(LaunchCommand), "launch")]
[JsonDerivedType(typeof(HomeCommand), "home")]
[JsonDerivedType(typeof(SleepCommand), "sleep")]
[JsonDerivedType(typeof(DesktopCommand), "desktop")]
[JsonDerivedType(typeof(SeekCommand), "seek")]
[JsonDerivedType(typeof(CloseCommand), "close")]
[JsonDerivedType(typeof(SplitCommand), "split")]
public abstract record RemoteCommand
{
    /// <summary>Returns null for malformed or unknown messages rather than throwing.</summary>
    public static RemoteCommand? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RemoteCommand>(json, HomeTvJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            // Missing or unknown "type" discriminator.
            return null;
        }
    }
}

public sealed record KeyCommand(RemoteKey Key) : RemoteCommand;

public sealed record MouseMoveCommand(int Dx, int Dy) : RemoteCommand;

public sealed record ClickCommand(MouseButton Button = MouseButton.Left) : RemoteCommand;

public sealed record ScrollCommand(int Dy) : RemoteCommand;

public sealed record TextCommand(string Text) : RemoteCommand;

/// <summary>Opens an app. <paramref name="Continue"/> is a "Continue watching" entry id: open that page instead of the start page.</summary>
public sealed record LaunchCommand(string App, string? Continue = null) : RemoteCommand;

/// <summary>Jumps the playing media to <paramref name="Position"/> seconds.</summary>
public sealed record SeekCommand(double Position) : RemoteCommand;

public sealed record HomeCommand : RemoteCommand;

public sealed record SleepCommand : RemoteCommand;

public sealed record DesktopCommand : RemoteCommand;

/// <summary>Closes an open app, in front or in the background.</summary>
public sealed record CloseCommand(string App) : RemoteCommand;

/// <summary>Tiles 2–4 apps side by side. Fewer ends split screen, leaving that app (or the active tile) full screen.</summary>
public sealed record SplitCommand(IReadOnlyList<string> Apps) : RemoteCommand;
