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

    /// <summary>
    /// Edge profile to use. Null means the user's default profile. An app with its own profile gets a DevTools port,
    /// which HomeTV needs to pause it in the background, tile and mute it in split screen, and read its pages.
    /// </summary>
    public string? EdgeProfile { get; init; }

    /// <summary>User-agent override for sites that serve a different UI per device.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Switch Windows HDR on while this app is in front.</summary>
    public bool Hdr { get; init; }

    /// <summary>
    /// Keep playing when you leave the app for another one or for the launcher (music). Otherwise an Edge app's
    /// video pauses when it goes into the background.
    /// </summary>
    public bool PlayInBackground { get; init; }

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

    /// <summary>
    /// Edge apps only: a regex for page URLs worth offering under "Continue watching", e.g. <c>youtube\.com/watch</c>.
    /// Pages are only remembered once a video has played on them. Leave unset to keep the app out of the row.
    /// Needs <see cref="EdgeProfile"/>, because Edge only allows the DevTools port HomeTV reads URLs through on a
    /// non-default profile.
    /// </summary>
    public string? ResumePattern { get; init; }

    /// <summary>Query parameter that starts the video at a time in seconds when resuming, e.g. <c>t</c> for YouTube.</summary>
    public string? ResumeTimeParam { get; init; }

    /// <summary>
    /// Edge apps with a profile: a page for signing in to the site, which Settings opens in a normal Edge window
    /// without the DevTools port. Google refuses to sign in to a browser that has the port open ("This browser or
    /// app may not be secure"), so YouTube needs this once; the sign-in sticks to the profile afterwards.
    /// </summary>
    public string? SignInUrl { get; init; }

    /// <summary>
    /// A site you don't trust (free streaming sites full of pop-up ads). Edge apps with a profile only: any extra
    /// tab or window the page opens is closed at once, and HomeTV never opens the sign-in page for it.
    /// </summary>
    public bool Untrusted { get; init; }

    /// <summary>Found running rather than declared in apps.json (a Steam game): not a launcher tile.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Discovered { get; init; }
}
