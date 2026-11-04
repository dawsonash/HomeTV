using System.Text.Json.Serialization;
using HomeTV.Core.Platform;

namespace HomeTV.Core.Session;

/// <summary>Server → client messages on the launcher and remote WebSockets.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StateEvent), "state")]
[JsonDerivedType(typeof(LauncherKeyEvent), "key")]
[JsonDerivedType(typeof(PinShownEvent), "pinShown")]
[JsonDerivedType(typeof(PinHiddenEvent), "pinHidden")]
[JsonDerivedType(typeof(NowPlayingEvent), "nowPlaying")]
[JsonDerivedType(typeof(TextFocusEvent), "textFocus")]
[JsonDerivedType(typeof(ScrollableEvent), "scrollable")]
[JsonDerivedType(typeof(AudioEvent), "audio")]
public abstract record HubEvent;

/// <summary>Where sound can go and where it's going now (<paramref name="Current"/> is null for a device not in audio.json).</summary>
public sealed record AudioEvent(IReadOnlyList<Audio.AudioOutputInfo> Outputs, string? Current) : HubEvent;

/// <summary>What's playing changed (or stopped: <paramref name="Media"/> is null).</summary>
public sealed record NowPlayingEvent(NowPlaying.NowPlayingInfo? Media) : HubEvent;

/// <summary>Focus on the TV moved into a text field, or out of one (<paramref name="Field"/> is null).</summary>
public sealed record TextFocusEvent(TextField? Field) : HubEvent;

/// <summary>Whether there's anything to scroll under the TV's pointer (the phone shows its scroll strip if so).</summary>
public sealed record ScrollableEvent(bool Scrollable) : HubEvent;

/// <summary>What's on screen and what's open.</summary>
/// <param name="ForegroundApp">The app in front (the active tile in split screen), or null for the launcher.</param>
/// <param name="Running">Open apps, most recently used first.</param>
/// <param name="Split">The tiled apps in screen order, or null outside split screen.</param>
public sealed record StateEvent(string? ForegroundApp, IReadOnlyList<string> Running, IReadOnlyList<string>? Split) : HubEvent;

public sealed record LauncherKeyEvent(RemoteKey Key) : HubEvent;

public sealed record PinShownEvent(string Pin, DateTimeOffset ExpiresAt) : HubEvent;

public sealed record PinHiddenEvent : HubEvent;
