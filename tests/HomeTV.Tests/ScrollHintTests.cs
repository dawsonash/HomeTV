using HomeTV.Core.Remote;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeTV.Tests;

public class ScrollHintTests
{
    private readonly RecordingAppHost _host = new();
    private readonly SessionState _session = new();
    private readonly ScrollHint _hint;

    public ScrollHintTests() => _hint = new ScrollHint(_host, _session, NullLogger<ScrollHint>.Instance);

    private List<bool> Published(SessionState.Subscription subscription)
    {
        var events = new List<bool>();
        while (subscription.Reader.TryRead(out var evt))
            if (evt is ScrollableEvent scrollable)
                events.Add(scrollable.Scrollable);
        return events;
    }

    [Fact]
    public async Task Launcher_never_scrolls()
    {
        _host.CanScroll = true;
        using var subscription = _session.Subscribe();

        await _hint.RefreshAsync();

        Assert.Equal([false], Published(subscription));
        Assert.False(_hint.Scrollable);
    }

    [Fact]
    public async Task Follows_the_page_and_only_says_so_when_it_changes()
    {
        _session.SetApps("youtube", ["youtube"], null);
        using var subscription = _session.Subscribe();

        _host.CanScroll = true;
        await _hint.RefreshAsync();
        await _hint.RefreshAsync();
        _host.CanScroll = false;
        await _hint.RefreshAsync();

        Assert.Equal([true, false], Published(subscription));
    }

    [Fact]
    public async Task Keeps_the_strip_where_it_cannot_tell()
    {
        _session.SetApps("games", ["games"], null);
        _host.CanScroll = null;

        await _hint.RefreshAsync();

        Assert.True(_hint.Scrollable);
    }
}
