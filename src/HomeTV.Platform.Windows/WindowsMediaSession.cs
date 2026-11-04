using HomeTV.Core.NowPlaying;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using WinRtPlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace HomeTV.Platform.Windows;

/// <summary>
/// Reads Windows' current media session (what the volume flyout shows): Edge apps, Spotify and most other players
/// report title, artist, artwork and position to it, and accept seeks through it when they support seeking.
/// </summary>
public sealed class WindowsMediaSession(ILogger<WindowsMediaSession> logger) : IMediaSession, IDisposable
{
    private const ulong MaxArtBytes = 2 * 1024 * 1024;

    private readonly Lock _lock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Artwork is only reloaded when the track changes (or arrives late, as Edge's often does).</summary>
    private (string Key, byte[]? Bytes, string? Type) _art = ("", null, null);

    public event Action? Changed;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        Attach();
    }

    public async Task<MediaReading?> ReadAsync(CancellationToken ct = default)
    {
        var session = _manager?.GetCurrentSession();
        if (session is null)
            return null;

        var playback = session.GetPlaybackInfo();
        if (playback is null || playback.PlaybackStatus == WinRtPlaybackStatus.Closed)
            return null;
        var status = playback.PlaybackStatus switch
        {
            WinRtPlaybackStatus.Playing => PlaybackStatus.Playing,
            WinRtPlaybackStatus.Paused => PlaybackStatus.Paused,
            _ => PlaybackStatus.Stopped,
        };

        var properties = await session.TryGetMediaPropertiesAsync().AsTask(ct);
        var timeline = session.GetTimelineProperties();

        double? duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
        if (duration is not > 0)
            duration = null;
        double? position = null;
        if (duration is { } d)
        {
            var at = timeline.Position.TotalSeconds;
            // Players report the position as of LastUpdatedTime; bring it up to now.
            if (status == PlaybackStatus.Playing && timeline.LastUpdatedTime.Year > 2000)
                at += Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds);
            position = Math.Clamp(at, 0, d);
        }

        var source = session.SourceAppUserModelId ?? "";
        var art = await ReadArtAsync(source, properties, ct);

        return new MediaReading(
            source,
            Blank(properties.Title),
            Blank(properties.Artist) ?? Blank(properties.AlbumArtist),
            Blank(properties.AlbumTitle),
            status,
            position,
            duration,
            playback.Controls.IsPlaybackPositionEnabled && duration is not null,
            art.Bytes,
            art.Type);
    }

    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        var session = _manager?.GetCurrentSession();
        if (session is null)
            return false;
        var ok = await session.TryChangePlaybackPositionAsync(position.Ticks).AsTask(ct);
        if (!ok)
            logger.LogInformation("{Source} didn't accept a seek", session.SourceAppUserModelId);
        return ok;
    }

    public void Dispose()
    {
        if (_manager is not null)
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        lock (_lock)
            Detach();
    }

    private async Task<(byte[]? Bytes, string? Type)> ReadArtAsync(
        string source, GlobalSystemMediaTransportControlsSessionMediaProperties properties, CancellationToken ct)
    {
        var key = $"{source}\n{properties.Title}\n{properties.Artist}\n{properties.AlbumTitle}";
        lock (_lock)
        {
            if (_art.Key == key && (_art.Bytes is not null || properties.Thumbnail is null))
                return (_art.Bytes, _art.Type);
        }

        byte[]? bytes = null;
        string? type = null;
        if (properties.Thumbnail is { } thumbnail)
        {
            try
            {
                using var stream = await thumbnail.OpenReadAsync().AsTask(ct);
                if (stream.Size is > 0 and <= MaxArtBytes)
                {
                    using var copy = new MemoryStream((int)stream.Size);
                    await stream.AsStreamForRead().CopyToAsync(copy, ct);
                    bytes = copy.ToArray();
                    type = string.IsNullOrEmpty(stream.ContentType) ? "image/jpeg" : stream.ContentType;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Couldn't read artwork from {Source}", source);
            }
        }

        lock (_lock)
            _art = (key, bytes, type);
        return (bytes, type);
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        Attach();
        Changed?.Invoke();
    }

    /// <summary>Listens to the current session's own change events (and stops listening to the previous one's).</summary>
    private void Attach()
    {
        var next = _manager?.GetCurrentSession();
        lock (_lock)
        {
            Detach();
            _session = next;
            if (next is null)
                return;
            next.MediaPropertiesChanged += OnSessionChanged;
            next.PlaybackInfoChanged += OnSessionChanged;
            next.TimelinePropertiesChanged += OnSessionChanged;
        }
    }

    private void Detach()
    {
        if (_session is null)
            return;
        _session.MediaPropertiesChanged -= OnSessionChanged;
        _session.PlaybackInfoChanged -= OnSessionChanged;
        _session.TimelinePropertiesChanged -= OnSessionChanged;
        _session = null;
    }

    private void OnSessionChanged<T>(GlobalSystemMediaTransportControlsSession sender, T args) => Changed?.Invoke();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
