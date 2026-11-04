using System.Globalization;
using System.Text.Json;

namespace HomeTV.Core.Widgets;

/// <summary>Open-Meteo forecast API: free, no key, CC BY 4.0 (credited on the weather card).</summary>
public static class OpenMeteo
{
    public static string Url(WeatherConfig config) =>
        FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={config.Latitude}&longitude={config.Longitude}")
        + "&current=temperature_2m,weather_code,is_day"
        + "&hourly=temperature_2m,weather_code,is_day,precipitation_probability"
        + "&daily=temperature_2m_max,temperature_2m_min"
        + $"&temperature_unit={(config.Units == TemperatureUnit.Celsius ? "celsius" : "fahrenheit")}"
        // 8 hours from the current one: the card shows 6 ahead, with slack for data cached across an hour boundary.
        + "&timezone=auto&forecast_days=1&forecast_hours=8";

    public static Weather Parse(string json, WeatherConfig config, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var current = root.GetProperty("current");

        // Times come back in the location's local time without an offset; this is that offset.
        var offset = TimeSpan.FromSeconds(root.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0);

        var hours = new List<WeatherHour>();
        if (root.TryGetProperty("hourly", out var hourly))
        {
            var times = hourly.GetProperty("time");
            var temps = hourly.GetProperty("temperature_2m");
            var codes = hourly.GetProperty("weather_code");
            var isDays = hourly.TryGetProperty("is_day", out var d) ? d : default;
            var precip = hourly.TryGetProperty("precipitation_probability", out var p) ? p : default;
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                // Values can come back null at the edge of the forecast; skip that hour rather than fail the card.
                if (temps[i].ValueKind != JsonValueKind.Number || codes[i].ValueKind != JsonValueKind.Number)
                    continue;
                var local = DateTime.ParseExact(times[i].GetString()!, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
                hours.Add(new WeatherHour(
                    new DateTimeOffset(local, offset),
                    temps[i].GetDouble(),
                    codes[i].GetInt32(),
                    NumberAt(isDays, i) == 1,
                    (int?)NumberAt(precip, i)));
            }
        }

        double? high = null, low = null;
        if (root.TryGetProperty("daily", out var daily))
        {
            high = daily.TryGetProperty("temperature_2m_max", out var max) ? NumberAt(max, 0) : null;
            low = daily.TryGetProperty("temperature_2m_min", out var min) ? NumberAt(min, 0) : null;
        }

        return new Weather(
            config.Place,
            config.Units == TemperatureUnit.Celsius ? "C" : "F",
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.TryGetProperty("is_day", out var isDay) && isDay.GetInt32() == 1,
            high,
            low,
            hours,
            now);
    }

    private static double? NumberAt(JsonElement array, int index) =>
        array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetDouble()
            : null;
}

/// <summary>
/// ESPN's public site API. Undocumented, so everything is read defensively, and kept in this one class so
/// a change on their side (or a switch to another provider) touches nothing else.
/// </summary>
public static class Espn
{
    public static string Url(TeamConfig team) =>
        $"https://site.api.espn.com/apis/site/v2/sports/{team.Sport}/{team.League}/teams/{team.Team}";

    public static TeamStatus Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var team = doc.RootElement.GetProperty("team");
        var id = Str(team, "id");
        var abbreviation = Str(team, "abbreviation") ?? "?";

        string? record = null;
        if (team.TryGetProperty("record", out var rec) && rec.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            record = Str(items[0], "summary");

        Game? game = null;
        if (team.TryGetProperty("nextEvent", out var events) && events.ValueKind == JsonValueKind.Array && events.GetArrayLength() > 0)
            game = ParseGame(events[0], id, abbreviation);

        return new TeamStatus(abbreviation, Str(team, "displayName") ?? abbreviation, Str(team, "color"), record, game, now, Logo: Logo(team));
    }

    public const string ImageHost = "a.espncdn.com";

    /// <summary>
    /// The "dark" logo (made for dark backgrounds), else the default one, resized by ESPN's image service to
    /// 96px (~5 KB instead of ~40 KB). Anything not on ESPN's image host is dropped: the URL ends up in an img tag.
    /// </summary>
    private static string? Logo(JsonElement team)
    {
        if (!team.TryGetProperty("logos", out var logos) || logos.ValueKind != JsonValueKind.Array)
            return null;

        string? dark = null, fallback = null;
        foreach (var logo in logos.EnumerateArray())
        {
            var rel = logo.TryGetProperty("rel", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet()
                : [];
            var href = Str(logo, "href");
            if (href is null || rel.Contains("scoreboard"))
                continue;
            if (rel.Contains("dark"))
                dark ??= href;
            else if (rel.Contains("default"))
                fallback ??= href;
        }

        if ((dark ?? fallback) is not { } chosen
            || !Uri.TryCreate(chosen, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != ImageHost
            || !uri.AbsolutePath.StartsWith("/i/teamlogos/", StringComparison.Ordinal))
            return null;
        return $"https://{ImageHost}/combiner/i?img={Uri.EscapeDataString(uri.AbsolutePath)}&w=96&h=96";
    }

    private static Game? ParseGame(JsonElement evt, string? teamId, string abbreviation)
    {
        if (!evt.TryGetProperty("competitions", out var comps) || comps.GetArrayLength() == 0)
            return null;
        var comp = comps[0];
        if (!comp.TryGetProperty("competitors", out var competitors))
            return null;

        JsonElement? us = null, them = null;
        foreach (var c in competitors.EnumerateArray())
        {
            var t = c.GetProperty("team");
            var isUs = teamId is not null ? Str(t, "id") == teamId : Str(t, "abbreviation") == abbreviation;
            if (isUs) us = c; else them = c;
        }
        if (us is null || them is null)
            return null;

        var status = comp.TryGetProperty("status", out var s) && s.TryGetProperty("type", out var st) ? st : default;
        var state = (status.ValueKind == JsonValueKind.Object ? Str(status, "state") : null) switch
        {
            "in" => GameState.Live,
            "post" => GameState.Final,
            _ => GameState.Scheduled,
        };

        var dateText = Str(comp, "date") ?? Str(evt, "date");
        if (!DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var startsAt))
            return null;

        string? broadcast = null;
        if (comp.TryGetProperty("broadcasts", out var casts) && casts.ValueKind == JsonValueKind.Array)
        {
            // Up to two channels, skipping variants of one already listed ("Spectrum Sports Net +").
            var names = new List<string>();
            foreach (var name in casts.EnumerateArray().Select(b => b.TryGetProperty("media", out var m) ? Str(m, "shortName") : null).OfType<string>())
            {
                if (names.Count < 2 && !names.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                    names.Add(name);
            }
            if (names.Count > 0)
                broadcast = string.Join(" / ", names);
        }

        var opponent = them.Value.GetProperty("team");
        var opponentId = Str(opponent, "id");
        return new Game(
            state,
            startsAt,
            status.ValueKind == JsonValueKind.Object ? Str(status, "shortDetail") ?? "" : "",
            Str(us.Value, "homeAway") == "home",
            Str(opponent, "abbreviation") ?? "?",
            state == GameState.Scheduled ? null : Score(us.Value),
            state == GameState.Scheduled ? null : Score(them.Value),
            broadcast,
            OpponentName: Str(opponent, "displayName"),
            OpponentLogo: Logo(opponent),
            // Only finished games carry the competitors' records; WidgetService fills in the rest.
            OpponentRecord: CompetitorRecord(them.Value),
            // Goes into an ESPN URL path when looking up that record, so digits only.
            OpponentId: opponentId is not null && opponentId.All(char.IsAsciiDigit) ? opponentId : null);
    }

    private static string? CompetitorRecord(JsonElement competitor)
    {
        if (!competitor.TryGetProperty("record", out var records) || records.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var r in records.EnumerateArray())
        {
            if (Str(r, "type") == "total" || Str(r, "abbreviation") == "Game")
                return Str(r, "displayValue");
        }
        return null;
    }

    // The team endpoint gives {"value": 17, "displayValue": "17"}; scoreboards give "17". Accept both.
    private static int? Score(JsonElement competitor)
    {
        if (!competitor.TryGetProperty("score", out var score))
            return null;
        if (score.ValueKind == JsonValueKind.Object && score.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
            return (int)v.GetDouble();
        if (score.ValueKind == JsonValueKind.String && int.TryParse(score.GetString(), CultureInfo.InvariantCulture, out var n))
            return n;
        return null;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
