using HomeTV.Core.Apps;
using HomeTV.Core.Continue;
using HomeTV.Core.Platform;

namespace HomeTV.Tests;

/// <summary>An <see cref="IAppHost"/> that writes what it was asked to do into <see cref="Calls"/>.</summary>
internal sealed class RecordingAppHost(List<string>? calls = null) : IAppHost
{
    public List<string> Calls { get; } = calls ?? [];

    /// <summary>Apps whose window is open.</summary>
    public HashSet<string> Open { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps whose window never appears.</summary>
    public HashSet<string> NeverOpens { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task LaunchAsync(AppDefinition app, string? url = null, CancellationToken ct = default)
    {
        Calls.Add(url is null ? $"launch {app.Id}" : $"launch {app.Id} {url}");
        if (!NeverOpens.Contains(app.Id))
            Open.Add(app.Id);
        return Task.CompletedTask;
    }

    public Task<bool> WaitForWindowAsync(string appId, TimeSpan timeout, CancellationToken ct = default) =>
        Task.FromResult(Open.Contains(appId));

    public Task<bool> ShowAsync(string appId, CancellationToken ct = default)
    {
        Calls.Add($"show {appId}");
        return Task.FromResult(Open.Contains(appId));
    }

    public Task ShowLauncherAsync(CancellationToken ct = default)
    {
        Calls.Add("launcher");
        return Task.CompletedTask;
    }

    public Task CloseAsync(string appId, CancellationToken ct = default)
    {
        Calls.Add($"close {appId}");
        Open.Remove(appId);
        return Task.CompletedTask;
    }

    public Task PauseAsync(string appId, CancellationToken ct = default)
    {
        Calls.Add($"pause {appId}");
        return Task.CompletedTask;
    }

    public Task ArrangeAsync(IReadOnlyList<string> appIds, string active, CancellationToken ct = default)
    {
        Calls.Add($"tile {string.Join(",", appIds)} active {active}");
        return Task.CompletedTask;
    }

    public void OpenDesktop() => Calls.Add("desktop");

    /// <summary>What <see cref="CanScrollAtPointerAsync"/> answers.</summary>
    public bool? CanScroll { get; set; }

    public Task<bool?> CanScrollAtPointerAsync(CancellationToken ct = default) => Task.FromResult(CanScroll);

    public event Action<string>? AppExited { add { } remove { } }

    public event Action<string>? AppActivated { add { } remove { } }

    public event Action<string, PageSnapshot>? PageObserved { add { } remove { } }
}
