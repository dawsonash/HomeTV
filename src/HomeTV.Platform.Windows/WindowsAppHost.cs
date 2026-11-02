using System.Diagnostics;
using HomeTV.Core.Apps;
using HomeTV.Core.Platform;
using Microsoft.Extensions.Logging;

namespace HomeTV.Platform.Windows;

/// <summary>
/// Launches apps. Window tracking (Home closing the app, refocusing the launcher) lands in phase 3 with the
/// WebView2 launcher window; until then Home only updates state, and the app stays open.
/// </summary>
public sealed class WindowsAppHost(ILogger<WindowsAppHost> logger) : IAppHost
{
    private static readonly string EdgePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");

    private static readonly string ProfilesRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTV", "edge-profiles");

    public Task LaunchAsync(AppDefinition app, CancellationToken ct = default)
    {
        var startInfo = app.Kind switch
        {
            AppKind.Edge => EdgeStartInfo(app),
            AppKind.Shell => new ProcessStartInfo(app.Target) { UseShellExecute = true },
            _ => throw new ArgumentOutOfRangeException(nameof(app), app.Kind, null),
        };

        logger.LogInformation("Launching {App}: {File} {Args}", app.Name, startInfo.FileName, string.Join(' ', startInfo.ArgumentList));
        Process.Start(startInfo);
        return Task.CompletedTask;
    }

    public Task ReturnHomeAsync(CancellationToken ct = default)
    {
        logger.LogWarning("Return home: closing app windows is not implemented yet (phase 3)");
        return Task.CompletedTask;
    }

    public void OpenDesktop() => Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });

    private static ProcessStartInfo EdgeStartInfo(AppDefinition app)
    {
        var startInfo = new ProcessStartInfo(EdgePath);
        startInfo.ArgumentList.Add($"--app={app.Target}");
        startInfo.ArgumentList.Add("--start-fullscreen");
        startInfo.ArgumentList.Add("--no-first-run");
        if (app.EdgeProfile is not null)
            startInfo.ArgumentList.Add($"--user-data-dir={Path.Combine(ProfilesRoot, app.EdgeProfile)}");
        if (app.UserAgent is not null)
            startInfo.ArgumentList.Add($"--user-agent={app.UserAgent}");
        return startInfo;
    }
}
