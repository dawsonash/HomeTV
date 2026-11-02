using HomeTV.Core.Apps;

namespace HomeTV.Core.Platform;

/// <summary>Starts and stops the full-screen apps (Edge windows, Steam) that sit on top of the launcher.</summary>
public interface IAppHost
{
    Task LaunchAsync(AppDefinition app, CancellationToken ct = default);

    /// <summary>Closes the foreground app and brings the launcher back to the front.</summary>
    Task ReturnHomeAsync(CancellationToken ct = default);

    /// <summary>Maintenance escape hatch: starts Explorer on top of the HomeTV shell.</summary>
    void OpenDesktop();
}
