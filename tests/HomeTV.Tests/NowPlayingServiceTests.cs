using HomeTV.Core.NowPlaying;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeTV.Tests;

public class NowPlayingServiceTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly ScriptedSession _media = new();
    private readonly SessionState _session = new();
    private readonly NowPlayingService _service;

    public NowPlayingServiceTests() =>
        _service = new NowPlayingService(_media, _session, _time, NullLogger<NowPlayingService>.Instance);

    private static MediaReading Song(double position, PlaybackStatus status = PlaybackStatus.Playing, byte[]? art = null) =>
        new("Spotify.exe", "Song", "Artist", "Album", status, position, 200, true, art, art is null ? null : "image/png");

    private List<NowPlayingEvent> Published(SessionState.Subscription subscription)
    {
        var events = new List<NowPlayingEvent>();
        while (subscription.Reader.TryRead(out var evt))
            events.Add(Assert.IsType<NowPlayingEvent>(evt));
        return events;
    }

    [Fact]
    public async Task Publishes_when_playback_starts_and_stops()
    {
        using var subscription = _session.Subscribe();

        _media.Next = Song(10);
        await _service.RefreshAsync();
        _media.Next = null;
        await _service.RefreshAsync();

        var events = Published(subscription);
        Assert.Equal(2, events.Count);
        Assert.Equal("Song", events[0].Media!.Title);
        Assert.Null(events[1].Media);
    }

    [Fact]
    public async Task Normal_playback_progress_is_not_news_but_a_seek_is()
    {
        _media.Next = Song(10);
        await _service.RefreshAsync();
        using var subscription = _session.Subscribe();

        _time.Advance(TimeSpan.FromSeconds(5));
        _media.Next = Song(15.2);
        await _service.RefreshAsync();
        Assert.Empty(Published(subscription));

        _media.Next = Song(120);
        await _service.RefreshAsync();
        Assert.Equal(120, Assert.Single(Published(subscription)).Media!.Position);
    }

    [Fact]
    public async Task Pausing_is_news_and_current_position_advances_only_while_playing()
    {
        _media.Next = Song(10);
        await _service.RefreshAsync();
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(13, _service.Current!.Position);

        using var subscription = _session.Subscribe();
        _media.Next = Song(13, PlaybackStatus.Paused);
        await _service.RefreshAsync();
        Assert.Single(Published(subscription));

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(13, _service.Current!.Position);
    }

    [Fact]
    public async Task Artwork_gets_a_version_that_changes_with_the_image()
    {
        _media.Next = Song(10, art: [1, 2, 3]);
        await _service.RefreshAsync();
        var first = _service.Current!.ArtVersion;

        _media.Next = Song(10, art: [4, 5, 6]);
        await _service.RefreshAsync();

        Assert.NotNull(first);
        Assert.NotEqual(first, _service.Current!.ArtVersion);
        Assert.Equal([4, 5, 6], _service.Art!.Value.Bytes);
        Assert.Equal("image/png", _service.Art.Value.Type);
    }

    [Fact]
    public async Task Seek_is_clamped_to_the_duration()
    {
        _media.Next = Song(10);
        await _service.RefreshAsync();

        await _service.SeekAsync(500);

        Assert.Equal(TimeSpan.FromSeconds(200), _media.LastSeek);
    }

    private sealed class ScriptedSession : IMediaSession
    {
        public MediaReading? Next { get; set; }
        public TimeSpan? LastSeek { get; private set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
        public Task<MediaReading?> ReadAsync(CancellationToken ct = default) => Task.FromResult(Next);

        public Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
        {
            LastSeek = position;
            return Task.FromResult(true);
        }
    }
}
