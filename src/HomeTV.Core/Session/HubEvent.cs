using System.Text.Json.Serialization;
using HomeTV.Core.Platform;

namespace HomeTV.Core.Session;

/// <summary>Server → client messages on the launcher and remote WebSockets.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StateEvent), "state")]
[JsonDerivedType(typeof(LauncherKeyEvent), "key")]
[JsonDerivedType(typeof(PinShownEvent), "pinShown")]
[JsonDerivedType(typeof(PinHiddenEvent), "pinHidden")]
public abstract record HubEvent;

public sealed record StateEvent(string? ForegroundApp) : HubEvent;

public sealed record LauncherKeyEvent(RemoteKey Key) : HubEvent;

public sealed record PinShownEvent(string Pin, DateTimeOffset ExpiresAt) : HubEvent;

public sealed record PinHiddenEvent : HubEvent;
