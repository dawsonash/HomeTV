using HomeTV.Core.Platform;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging;

namespace HomeTV.Core.Remote;

/// <summary>
/// Tells the phone whether there's anything to scroll under the TV's pointer, so it only shows its scroll strip
/// when there is. On the launcher there never is; where HomeTV can't tell (Steam, Spotify), the strip stays.
/// </summary>
public sealed class ScrollHint(IAppHost host, SessionState session, ILogger<ScrollHint> logger) : IDisposable
{
    /// <summary>How often to look. Each look is cheap: the app host reuses its answer while the pointer stays put.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(700);

    private readonly CancellationTokenSource _stop = new();
    private bool? _current;

    /// <summary>Whether the phone should show its scroll strip; true until the first look.</summary>
    public bool Scrollable => _current ?? true;

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>Looks once, and tells clients if the answer changed.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var scrollable = !session.LauncherInFront && (await host.CanScrollAtPointerAsync(ct) ?? true);
        if (scrollable == _current)
            return;
        _current = scrollable;
        session.Publish(new ScrollableEvent(scrollable));
    }

    public void Dispose() => _stop.Cancel();

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't tell whether the page under the pointer scrolls");
                await Task.Delay(Interval * 5, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
