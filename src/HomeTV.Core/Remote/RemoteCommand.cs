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

public sealed record LaunchCommand(string App) : RemoteCommand;

public sealed record HomeCommand : RemoteCommand;

public sealed record SleepCommand : RemoteCommand;

public sealed record DesktopCommand : RemoteCommand;
