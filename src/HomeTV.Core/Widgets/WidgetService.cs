using Microsoft.Extensions.Logging;

namespace HomeTV.Core.Widgets;

/// <summary>
/// Fetches and caches widget data. The launcher polls <see cref="GetAsync"/>; outside calls happen only when the
/// cached data is due, so polling often is cheap. On failure the last good data is kept and retried later.
/// </summary>
public sealed class WidgetService(WidgetConfig config, HttpClient http, TimeProvider time, ILogger<WidgetService> logger)
{
    public static readonly TimeSpan WeatherRefresh = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SportsRefresh = TimeSpan.FromMinutes(10);
    /// <summary>While one of the teams is playing, or about to.</summary>
    public static readonly TimeSpan LiveRefresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan OpponentRecordRefresh = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private Weather? _weather;
    private DateTimeOffset _weatherDue = DateTimeOffset.MinValue;
    private readonly Dictionary<TeamConfig, TeamStatus> _teams = [];
    private DateTimeOffset _sportsDue = DateTimeOffset.MinValue;
    private readonly Dictionary<TeamConfig, (string? Record, DateTimeOffset Due)> _opponentRecords = [];

    public async Task<WidgetSnapshot> GetAsync(CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (config.Weather is { } weather && now >= _weatherDue)
                await RefreshWeatherAsync(weather, ct);
            if (config.Teams.Count > 0 && now >= _sportsDue)
                await RefreshSportsAsync(ct);

            // Config order, skipping teams that have never loaded.
            var teams = config.Teams.Select(t => _teams.GetValueOrDefault(t)).OfType<TeamStatus>().ToList();
            return new WidgetSnapshot(_weather, teams);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private async Task RefreshWeatherAsync(WeatherConfig weather, CancellationToken ct)
    {
        try
        {
            var json = await http.GetStringAsync(OpenMeteo.Url(weather), ct);
            _weather = OpenMeteo.Parse(json, weather, time.GetUtcNow());
            _weatherDue = time.GetUtcNow() + WeatherRefresh;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Weather refresh failed: {Error}", ex.Message);
            _weatherDue = time.GetUtcNow() + RetryAfterError;
        }
    }

    private async Task RefreshSportsAsync(CancellationToken ct)
    {
        var failed = false;
        await Task.WhenAll(config.Teams.Select(async team =>
        {
            try
            {
                var json = await http.GetStringAsync(Espn.Url(team), ct);
                var status = Espn.Parse(json, time.GetUtcNow()) with { Sport = team.Sport };
                if (status.Game is { OpponentRecord: null, OpponentId: { } opponentId } game)
                {
                    var record = await OpponentRecordAsync(new TeamConfig(team.Sport, team.League, opponentId), ct);
                    status = status with { Game = game with { OpponentRecord = record } };
                }
                lock (_teams)
                    _teams[team] = status;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Sports refresh failed for {League}/{Team}: {Error}", team.League, team.Team, ex.Message);
                failed = true;
            }
        }));

        var now = time.GetUtcNow();
        var interval = _teams.Values.Any(t => IsLiveOrImminent(t.Game, now)) ? LiveRefresh : SportsRefresh;
        if (failed && interval > RetryAfterError)
            interval = RetryAfterError;
        _sportsDue = now + interval;
    }

    /// <summary>
    /// An upcoming opponent's record, from their own team page. Records only move after games, so this is kept
    /// for <see cref="OpponentRecordRefresh"/> rather than refetched with every 30-second live update.
    /// A failed lookup just leaves the record off.
    /// </summary>
    private async Task<string?> OpponentRecordAsync(TeamConfig opponent, CancellationToken ct)
    {
        lock (_opponentRecords)
        {
            if (_opponentRecords.TryGetValue(opponent, out var cached) && time.GetUtcNow() < cached.Due)
                return cached.Record;
        }

        string? record = null;
        try
        {
            record = Espn.Parse(await http.GetStringAsync(Espn.Url(opponent), ct), time.GetUtcNow()).Record;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Opponent record lookup failed for {League}/{Team}: {Error}", opponent.League, opponent.Team, ex.Message);
        }

        lock (_opponentRecords)
            _opponentRecords[opponent] = (record, time.GetUtcNow() + (record is null ? RetryAfterError : OpponentRecordRefresh));
        return record;
    }

    /// <summary>Live, or starting within 15 minutes (so the score appears as soon as it starts).</summary>
    public static bool IsLiveOrImminent(Game? game, DateTimeOffset now) =>
        game is { State: GameState.Live }
        || game is { State: GameState.Scheduled } && game.StartsAt - now < TimeSpan.FromMinutes(15) && now - game.StartsAt < TimeSpan.FromHours(4);
}
