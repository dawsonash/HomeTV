namespace HomeTV.Core.Apps;

public enum AppKind
{
    /// <summary>A website opened in a full-screen Edge app window.</summary>
    Edge,

    /// <summary>A URI or executable handed to the shell, e.g. <c>steam://open/bigpicture</c>.</summary>
    Shell,
}

/// <summary>One tile on the launcher, as declared in <c>config/apps.json</c>.</summary>
public sealed record AppDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AppKind Kind { get; init; }

    /// <summary>The page URL for <see cref="AppKind.Edge"/>, or the URI/command for <see cref="AppKind.Shell"/>.</summary>
    public required string Target { get; init; }

    /// <summary>Edge profile to use. Null means the user's default profile (where Prime/Fubo logins live).</summary>
    public string? EdgeProfile { get; init; }

    /// <summary>User-agent override for sites that serve a different UI per device.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Switch Windows HDR on while this app is in front.</summary>
    public bool Hdr { get; init; }

    /// <summary>Tile accent color (CSS color).</summary>
    public string? Color { get; init; }

    /// <summary>
    /// Process that owns the app's window, without ".exe". Defaults to <c>msedge</c> for <see cref="AppKind.Edge"/>.
    /// Shell apps need it so Home can close them and HomeTV notices when they exit.
    /// </summary>
    public string? WindowProcess { get; init; }

    /// <summary>
    /// Text the app's window title contains. When set, an already-open matching window counts (e.g. Steam Big
    /// Picture left open); when not, only a window that appears after launch does.
    /// </summary>
    public string? WindowTitle { get; init; }

    /// <summary>URI or command that closes the app gracefully, e.g. <c>steam://close/bigpicture</c>. Otherwise Home closes its window.</summary>
    public string? CloseTarget { get; init; }
}
