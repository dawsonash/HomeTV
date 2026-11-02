namespace HomeTV.Core.Watchdog;

/// <summary>
/// How long the watchdog waits before restarting HomeTV. Doubles while it keeps crashing soon after starting
/// (so a crash loop doesn't spin), and resets once a run has been stable for a while.
/// </summary>
public sealed class RestartPolicy(TimeProvider time)
{
    public static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);

    private DateTimeOffset _startedAt = time.GetUtcNow();
    private TimeSpan _nextDelay = MinDelay;

    public int Restarts { get; private set; }

    public void OnStarted() => _startedAt = time.GetUtcNow();

    /// <summary>Call when the app has exited; returns how long to wait before starting it again.</summary>
    public TimeSpan OnExited()
    {
        if (time.GetUtcNow() - _startedAt >= StableAfter)
            _nextDelay = MinDelay;

        var delay = _nextDelay;
        _nextDelay = TimeSpan.FromTicks(Math.Min(_nextDelay.Ticks * 2, MaxDelay.Ticks));
        Restarts++;
        return delay;
    }
}
