using System.Threading.Channels;
using HomeTV.Core.Platform;

namespace HomeTV.Core.Session;

/// <summary>
/// What's on the TV right now, plus a broadcast channel that the launcher and remotes listen on.
/// </summary>
public sealed class SessionState
{
    private readonly Lock _lock = new();
    private readonly List<Channel<HubEvent>> _subscribers = [];
    private StateEvent _state = new(null, [], null);

    /// <summary>Id of the app in front (the active tile in split screen), or null when the launcher is showing.</summary>
    public string? ForegroundAppId
    {
        get
        {
            lock (_lock)
                return _state.ForegroundApp;
        }
    }

    public bool LauncherInFront => ForegroundAppId is null;

    /// <summary>The current <see cref="StateEvent"/>, for a client that just connected.</summary>
    public StateEvent State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>Records what's on screen and what's open, and tells every client.</summary>
    /// <param name="running">Open apps, most recently used first.</param>
    /// <param name="split">The tiled apps in screen order, or null outside split screen.</param>
    public void SetApps(string? foreground, IReadOnlyList<string> running, IReadOnlyList<string>? split)
    {
        var state = new StateEvent(foreground, [.. running], split is null ? null : [.. split]);
        lock (_lock)
            _state = state;
        Publish(state);
    }

    /// <summary>Remote D-pad presses go straight to the launcher page instead of through OS input injection.</summary>
    public void SendKeyToLauncher(RemoteKey key) => Publish(new LauncherKeyEvent(key));

    public void Publish(HubEvent evt)
    {
        lock (_lock)
        {
            foreach (var channel in _subscribers)
                channel.Writer.TryWrite(evt);
        }
    }

    public Subscription Subscribe()
    {
        // Bounded so a stalled client can't grow memory; stale key presses are worthless anyway.
        var channel = Channel.CreateBounded<HubEvent>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock)
            _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    private void Unsubscribe(Channel<HubEvent> channel)
    {
        lock (_lock)
            _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    public sealed class Subscription(SessionState owner, Channel<HubEvent> channel) : IDisposable
    {
        public ChannelReader<HubEvent> Reader => channel.Reader;

        public void Dispose() => owner.Unsubscribe(channel);
    }
}
