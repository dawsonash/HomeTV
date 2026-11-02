using HomeTV.Core.Watchdog;

namespace HomeTV.Tests;

public class WatchdogTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void Crash_loop_backs_off_up_to_the_max()
    {
        var policy = new RestartPolicy(_time);
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            policy.OnStarted();
            _time.Advance(TimeSpan.FromSeconds(3)); // Crashes 3s after every start.
            delays.Add(policy.OnExited());
        }

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], delays.Select(d => (int)d.TotalSeconds));
        Assert.Equal(8, policy.Restarts);
    }

    [Fact]
    public void A_stable_run_resets_the_backoff()
    {
        var policy = new RestartPolicy(_time);
        for (var i = 0; i < 5; i++)
        {
            policy.OnStarted();
            policy.OnExited();
        }

        policy.OnStarted();
        _time.Advance(RestartPolicy.StableAfter);

        Assert.Equal(RestartPolicy.MinDelay, policy.OnExited());
    }

    [Fact]
    public void Failures_during_startup_are_not_a_hang()
    {
        var hang = new HangDetector(_time);
        hang.OnStarted();

        for (var i = 0; i < 10; i++)
            Assert.False(hang.Report(healthy: false));
    }

    [Fact]
    public void Consecutive_failures_after_startup_are_a_hang()
    {
        var hang = new HangDetector(_time);
        hang.OnStarted();
        _time.Advance(HangDetector.StartupGrace);

        Assert.False(hang.Report(false));
        Assert.False(hang.Report(false));
        Assert.True(hang.Report(false));
    }

    [Fact]
    public void A_good_check_clears_earlier_failures()
    {
        var hang = new HangDetector(_time);
        hang.OnStarted();
        _time.Advance(HangDetector.StartupGrace);

        hang.Report(false);
        hang.Report(false);
        hang.Report(true);

        Assert.False(hang.Report(false));
        Assert.False(hang.Report(false));
    }
}
