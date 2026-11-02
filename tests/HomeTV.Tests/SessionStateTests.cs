using HomeTV.Core.Session;

namespace HomeTV.Tests;

public class SessionStateTests
{
    private readonly SessionState _session = new();

    [Fact]
    public void App_exiting_brings_the_launcher_back()
    {
        _session.SetForeground("prime");
        using var subscription = _session.Subscribe();

        Assert.True(_session.ClearForeground("prime"));

        Assert.True(_session.LauncherInFront);
        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Null(Assert.IsType<StateEvent>(evt).ForegroundApp);
    }

    [Fact]
    public void Stale_exit_of_a_replaced_app_is_ignored()
    {
        // Prime was open, then YouTube was launched; Prime's window closing late must not hide YouTube.
        _session.SetForeground("youtube");
        using var subscription = _session.Subscribe();

        Assert.False(_session.ClearForeground("prime"));

        Assert.Equal("youtube", _session.ForegroundAppId);
        Assert.False(subscription.Reader.TryRead(out _));
    }
}
