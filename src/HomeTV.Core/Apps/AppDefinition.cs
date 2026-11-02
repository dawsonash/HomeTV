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

    /// <summary>User-agent override, e.g. a TV UA so youtube.com/tv serves the leanback UI.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Switch Windows HDR on while this app is in front.</summary>
    public bool Hdr { get; init; }

    /// <summary>Tile accent color (CSS color).</summary>
    public string? Color { get; init; }
}
