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

    /// <summary>Id of the app in front, or null when the launcher is showing.</summary>
    public string? ForegroundAppId { get; private set; }

    public bool LauncherInFront => ForegroundAppId is null;

    public void SetForeground(string? appId)
    {
        lock (_lock)
            ForegroundAppId = appId;
        Publish(new StateEvent(appId));
    }

    /// <summary>Shows the launcher again if <paramref name="appId"/> is still the app in front (it exited on its own).</summary>
    public bool ClearForeground(string appId)
    {
        lock (_lock)
        {
            if (!string.Equals(ForegroundAppId, appId, StringComparison.OrdinalIgnoreCase))
                return false;
            ForegroundAppId = null;
        }
        Publish(new StateEvent(null));
        return true;
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
