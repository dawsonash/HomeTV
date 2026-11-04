using HomeTV.Core.Session;

namespace HomeTV.Tests;

public class SessionStateTests
{
    private readonly SessionState _session = new();

    [Fact]
    public void Setting_apps_tells_clients_and_is_kept_for_new_ones()
    {
        using var subscription = _session.Subscribe();

        _session.SetApps("youtube", ["youtube", "prime"], null);

        Assert.Equal("youtube", _session.ForegroundAppId);
        Assert.False(_session.LauncherInFront);
        Assert.True(subscription.Reader.TryRead(out var evt));
        var state = Assert.IsType<StateEvent>(evt);
        Assert.Equal(["youtube", "prime"], state.Running);
        Assert.Null(state.Split);
        Assert.Same(state, _session.State);
    }

    [Fact]
    public void Starts_on_the_launcher_with_nothing_open()
    {
        Assert.True(_session.LauncherInFront);
        Assert.Empty(_session.State.Running);
    }
}
