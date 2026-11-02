namespace HomeTV.Core.Watchdog;

/// <summary>
/// Decides when a running-but-unresponsive HomeTV should be killed, from a stream of health check results.
/// Failures during startup don't count, and one slow check isn't a hang.
/// </summary>
public sealed class HangDetector(TimeProvider time)
{
    /// <summary>Startup time (WebView2 and the server coming up, Defender scanning a fresh deploy) before checks count.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    public const int FailuresBeforeHang = 3;

    private DateTimeOffset _startedAt = time.GetUtcNow();
    private int _failures;

    public void OnStarted()
    {
        _startedAt = time.GetUtcNow();
        _failures = 0;
    }

    /// <summary>Records one health check. Returns true when HomeTV should be treated as hung.</summary>
    public bool Report(bool healthy)
    {
        if (healthy)
        {
            _failures = 0;
            return false;
        }
        if (time.GetUtcNow() - _startedAt < StartupGrace)
            return false;
        return ++_failures >= FailuresBeforeHang;
    }
}
