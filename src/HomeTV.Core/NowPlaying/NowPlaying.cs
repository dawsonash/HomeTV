using System.Security.Cryptography;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging;

namespace HomeTV.Core.NowPlaying;

public enum PlaybackStatus
{
    Playing,
    Paused,
    Stopped,
}

/// <summary>What the OS's media controls report, as the platform reads it.</summary>
/// <param name="Source">The app playing it, as Windows names it (e.g. <c>MSEdge</c>, <c>Spotify.exe</c>).</param>
/// <param name="Position">Seconds, already advanced to the moment it was read.</param>
public sealed record MediaReading(
    string Source,
    string? Title,
    string? Artist,
    string? Album,
    PlaybackStatus Status,
    double? Position,
    double? Duration,
    bool CanSeek,
    byte[]? Art,
    string? ArtType);

/// <summary>The OS's "now playing" media session (on Windows, the one the volume flyout shows).</summary>
public interface IMediaSession
{
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Raised from any thread when something about the playing media may have changed.</summary>
    event Action? Changed;

    /// <summary>The current media, or null when nothing is playing or paused.</summary>
    Task<MediaReading?> ReadAsync(CancellationToken ct = default);

    Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default);
}

/// <summary>Sent to the phone remote. <see cref="ArtVersion"/> changes with the artwork, for cache-busting its URL.</summary>
public sealed record NowPlayingInfo(
    string Source,
    string? Title,
    string? Artist,
    string? Album,
    PlaybackStatus Status,
    double? Position,
    double? Duration,
    bool CanSeek,
    string? ArtVersion);

/// <summary>Keeps track of what's playing and tells remotes when it changes.</summary>
public sealed class NowPlayingService(IMediaSession media, SessionState session, TimeProvider time, ILogger<NowPlayingService> logger)
    : IDisposable
{
    /// <summary>Some players don't announce every change; look again this often regardless.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>Change notifications come in bursts (title, then art, then timeline); wait for them to settle.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    /// <summary>A position this far off where it should be by now counts as a seek, worth telling remotes about.</summary>
    private const double SeekThresholdSeconds = 2;

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Lock _lock = new();
    private NowPlayingInfo? _current;
    private DateTimeOffset _currentAt;
    private (byte[] Bytes, string Type)? _art;
    private int _pending;

    public NowPlayingInfo? Current
    {
        get
        {
            lock (_lock)
                return Advance(_current, _currentAt);
        }
    }

    /// <summary>The current artwork, if the player provided any.</summary>
    public (byte[] Bytes, string Type)? Art
    {
        get
        {
            lock (_lock)
                return _art;
        }
    }

    public async Task StartAsync()
    {
        try
        {
            await media.StartAsync(_stop.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Media controls unavailable; the remote won't show what's playing");
            return;
        }
        media.Changed += OnChanged;
        _ = Task.Run(RefreshLoopAsync);
        await RefreshAsync();
    }

    public Task<bool> SeekAsync(double seconds, CancellationToken ct = default)
    {
        var duration = Current?.Duration;
        var clamped = Math.Clamp(seconds, 0, duration ?? double.MaxValue);
        return double.IsFinite(clamped) ? media.SeekAsync(TimeSpan.FromSeconds(clamped), ct) : Task.FromResult(false);
    }

    /// <summary>Reads the session now and publishes a <see cref="NowPlayingEvent"/> if it changed. Public for tests.</summary>
    public async Task RefreshAsync()
    {
        if (_stop.IsCancellationRequested)
            return;
        await _refresh.WaitAsync();
        try
        {
            MediaReading? reading;
            try
            {
                reading = await media.ReadAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Players come and go mid-read; the next change or refresh tries again.
                logger.LogDebug(ex, "Couldn't read the media session");
                return;
            }

            var now = time.GetUtcNow();
            NowPlayingInfo? next;
            bool changed;
            lock (_lock)
            {
                var artVersion = reading?.Art is { Length: > 0 } bytes ? Convert.ToHexStringLower(SHA256.HashData(bytes))[..12] : null;
                _art = artVersion is not null ? (reading!.Art!, reading.ArtType ?? "image/jpeg") : null;

                next = reading is null
                    ? null
                    : new NowPlayingInfo(reading.Source, reading.Title, reading.Artist, reading.Album, reading.Status, reading.Position,
                        reading.Duration, reading.CanSeek, artVersion);
                changed = HasChanged(Advance(_current, _currentAt, now), next);
                _current = next;
                _currentAt = now;
            }
            if (changed)
                session.Publish(new NowPlayingEvent(next));
        }
        finally
        {
            _refresh.Release();
        }
    }

    public void Dispose()
    {
        media.Changed -= OnChanged;
        _stop.Cancel();
    }

    private void OnChanged()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(Debounce, time);
            Interlocked.Exchange(ref _pending, 0);
            await RefreshAsync();
        });
    }

    private async Task RefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
                await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private NowPlayingInfo? Advance(NowPlayingInfo? info, DateTimeOffset at) => Advance(info, at, time.GetUtcNow());

    /// <summary>Where a playing item's position is by <paramref name="now"/>, assuming it kept playing.</summary>
    private static NowPlayingInfo? Advance(NowPlayingInfo? info, DateTimeOffset at, DateTimeOffset now)
    {
        if (info is not { Status: PlaybackStatus.Playing, Position: { } position })
            return info;
        var advanced = position + (now - at).TotalSeconds;
        return info with { Position = info.Duration is { } d ? Math.Min(advanced, d) : advanced };
    }

    private static bool HasChanged(NowPlayingInfo? expected, NowPlayingInfo? actual)
    {
        if (expected is null || actual is null)
            return !ReferenceEquals(expected, actual);
        if (expected with { Position = null } != actual with { Position = null })
            return true;
        return (expected.Position, actual.Position) switch
        {
            (null, null) => false,
            ({ } e, { } a) => Math.Abs(e - a) > SeekThresholdSeconds,
            _ => true,
        };
    }
}
