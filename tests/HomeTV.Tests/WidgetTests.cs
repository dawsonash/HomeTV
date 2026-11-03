using System.Net;
using HomeTV.Core.Widgets;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeTV.Tests;

public class WidgetTests
{
    private static readonly WeatherConfig Seattle = new("Seattle", 47.61, -122.33);
    private static readonly TeamConfig Seahawks = new("football", "nfl", "sea");
    private readonly ManualTimeProvider _time = new();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Shipped_widgets_config_is_valid() =>
        WidgetConfig.Load(Path.Combine(AppContext.BaseDirectory, "widgets.json"));

    [Fact]
    public void Missing_config_file_means_no_widgets()
    {
        var config = WidgetConfig.Load(Path.Combine(AppContext.BaseDirectory, "no-such-widgets.json"));

        Assert.Null(config.Weather);
        Assert.Empty(config.Teams);
    }

    [Theory]
    [InlineData("""{"teams":[{"sport":"football","league":"nfl","team":"../../x"}]}""")]
    [InlineData("""{"weather":{"place":"x","latitude":91,"longitude":0}}""")]
    public void Bad_config_is_rejected(string json) =>
        Assert.Throws<InvalidOperationException>(() => WidgetConfig.Parse(json));

    [Fact]
    public void Open_meteo_response_parses()
    {
        var weather = OpenMeteo.Parse(Fixture("open-meteo.json"), Seattle, _time.Now);

        Assert.Equal("Seattle", weather.Place);
        Assert.Equal("F", weather.Units);
        Assert.Equal(44.6, weather.Temperature);
        Assert.Equal((55.5, 35.5), (weather.High, weather.Low));
        Assert.Equal(_time.Now, weather.UpdatedAt);

        // 8 hours from the current one, in the location's time zone (Denver, UTC-7, in the fixture).
        Assert.Equal(8, weather.Hours.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 7, 0, 0, TimeSpan.Zero), weather.Hours[0].Time.ToUniversalTime());
        Assert.Equal(51, weather.Hours[6].Code);
        Assert.Equal(34, weather.Hours[0].PrecipitationChance);
        Assert.True(weather.Hours[7].IsDay);
        Assert.False(weather.Hours[0].IsDay);
    }

    [Fact]
    public void Celsius_is_requested_when_configured() =>
        Assert.Contains("temperature_unit=celsius", OpenMeteo.Url(Seattle with { Units = TemperatureUnit.Celsius }));

    [Fact]
    public void Espn_finished_game_parses_with_score()
    {
        var team = Espn.Parse(Fixture("espn-team-final.json"), _time.Now);

        Assert.Equal("SEA", team.Abbreviation);
        Assert.Equal("002a5c", team.Color);
        Assert.Equal("https://a.espncdn.com/combiner/i?img=%2Fi%2Fteamlogos%2Fnfl%2F500-dark%2Fsea.png&w=96&h=96", team.Logo);
        var game = team.Game!;
        Assert.Equal(GameState.Final, game.State);
        Assert.Equal("CHI", game.Opponent);
        Assert.True(game.IsHome);
        Assert.Equal(17, game.Score);
        Assert.Equal(34, game.OpponentScore);
        Assert.Equal("ESPN", game.Broadcast);
        Assert.Equal("6-2", game.OpponentRecord); // Finished games carry both records.
        Assert.StartsWith("https://a.espncdn.com/combiner/i?img=", game.OpponentLogo);
    }

    [Fact]
    public void Espn_scheduled_game_parses_without_score()
    {
        var game = Espn.Parse(Fixture("espn-team-scheduled.json"), _time.Now).Game!;

        Assert.Equal(GameState.Scheduled, game.State);
        Assert.Equal("SAC", game.Opponent);
        Assert.False(game.IsHome);
        Assert.Null(game.Score);
        Assert.Equal("Spectrum Sports Net", game.Broadcast); // Not "... / Spectrum Sports Net +".
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.Zero), game.StartsAt);
    }

    [Fact]
    public void Espn_live_game_and_off_season_parse()
    {
        const string live = """
            {"team":{"id":"26","abbreviation":"SEA","displayName":"Seattle Seahawks","nextEvent":[{"date":"2026-11-03T01:15Z",
              "competitions":[{"date":"2026-11-03T01:15Z","status":{"type":{"state":"in","shortDetail":"Q3 5:32"}},
              "competitors":[{"homeAway":"away","team":{"id":"26","abbreviation":"SEA"},"score":"14"},
                             {"homeAway":"home","team":{"id":"25","abbreviation":"SF"},"score":"10"}]}]}]}}
            """;
        var game = Espn.Parse(live, _time.Now).Game!;
        Assert.Equal(GameState.Live, game.State);
        Assert.Equal("Q3 5:32", game.Detail);
        Assert.Equal((14, 10), (game.Score, game.OpponentScore));

        Assert.Null(Espn.Parse("""{"team":{"abbreviation":"SEA","nextEvent":[]}}""", _time.Now).Game);
    }

    [Theory]
    [InlineData("https://evil.example/i/teamlogos/x.png")]
    [InlineData("http://a.espncdn.com/i/teamlogos/nfl/500-dark/sea.png")]
    [InlineData("javascript:alert(1)")]
    public void Logos_off_espns_image_host_are_dropped(string href)
    {
        var json = $$$"""{"team":{"abbreviation":"SEA","logos":[{"rel":["full","dark"],"href":"{{{href}}}"}]}}""";
        Assert.Null(Espn.Parse(json, _time.Now).Logo);
    }

    [Fact]
    public async Task Data_is_cached_until_due_and_kept_when_a_refresh_fails()
    {
        var handler = new FakeHandler(Fixture("open-meteo.json"), Fixture("espn-team-scheduled.json"));
        var service = new WidgetService(new WidgetConfig(Seattle, [Seahawks]), new HttpClient(handler), _time, NullLogger<WidgetService>.Instance);

        var first = await service.GetAsync();
        await service.GetAsync();
        // Weather + team + the upcoming opponent's record; the second call was served entirely from cache.
        Assert.Equal(3, handler.Calls);
        Assert.NotNull(first.Weather);
        Assert.Equal("6-2", Assert.Single(first.Teams).Game!.OpponentRecord); // The fake serves the same team page.

        // A live refresh later must not look the opponent's record up again.
        _time.Advance(WidgetService.SportsRefresh);
        var second = await service.GetAsync();
        Assert.Equal(4, handler.Calls);

        handler.Fail = true;
        _time.Advance(WidgetService.WeatherRefresh);
        var afterFailure = await service.GetAsync();
        Assert.Equal(second.Weather, afterFailure.Weather);
        Assert.Equal(second.Teams, afterFailure.Teams);
    }

    [Fact]
    public void Sports_refresh_fast_only_around_live_games()
    {
        var now = _time.Now;
        Game At(GameState state, TimeSpan offset) => new(state, now + offset, "", true, "SF", null, null, null);

        Assert.True(WidgetService.IsLiveOrImminent(At(GameState.Live, TimeSpan.FromHours(-1)), now));
        Assert.True(WidgetService.IsLiveOrImminent(At(GameState.Scheduled, TimeSpan.FromMinutes(10)), now));
        Assert.False(WidgetService.IsLiveOrImminent(At(GameState.Scheduled, TimeSpan.FromHours(3)), now));
        Assert.False(WidgetService.IsLiveOrImminent(At(GameState.Final, TimeSpan.FromHours(-3)), now));
        Assert.False(WidgetService.IsLiveOrImminent(null, now));
    }

    private sealed class FakeHandler(string weatherJson, string teamJson) : HttpMessageHandler
    {
        public int Calls;
        public bool Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Fail)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = request.RequestUri!.Host.Contains("open-meteo") ? weatherJson : teamJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
