using System.Text.Json;

namespace HomeTV.Core.Widgets;

/// <summary>Home-screen widgets, as declared in <c>config/widgets.json</c>. Leave a section out to hide that widget.</summary>
public sealed record WidgetConfig(WeatherConfig? Weather = null, IReadOnlyList<TeamConfig>? Teams = null)
{
    public static readonly WidgetConfig Empty = new();

    /// <summary>Shown at most; more teams than this would not fit on the card.</summary>
    public const int MaxTeams = 4;

    public IReadOnlyList<TeamConfig> Teams { get; init; } = Teams ?? [];

    public static WidgetConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<WidgetConfig>(json, HomeTvJson.Options) ?? Empty;
        if (config.Weather is { } w && (w.Latitude is < -90 or > 90 || w.Longitude is < -180 or > 180))
            throw new InvalidOperationException("Widgets config: weather latitude/longitude out of range.");
        if (config.Teams.Count > MaxTeams)
            throw new InvalidOperationException($"Widgets config: at most {MaxTeams} teams.");
        foreach (var team in config.Teams)
        {
            if (!IsSlug(team.Sport) || !IsSlug(team.League) || !IsSlug(team.Team))
                throw new InvalidOperationException($"Widgets config: bad team entry {team}. Use ESPN slugs, e.g. football / nfl / sea.");
        }
        return config;
    }

    /// <summary>A missing file means no widgets, not an error: the home screen just shows the tiles.</summary>
    public static WidgetConfig Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;

    // The values go into ESPN URL paths, so keep them to plain slugs.
    private static bool IsSlug(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 40 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');
}

/// <param name="Place">Label shown on the card, e.g. "Seattle".</param>
public sealed record WeatherConfig(string Place, double Latitude, double Longitude, TemperatureUnit Units = TemperatureUnit.Fahrenheit);

public enum TemperatureUnit
{
    Fahrenheit,
    Celsius,
}

/// <summary>A team as ESPN names it in URLs: site.api.espn.com/apis/site/v2/sports/{Sport}/{League}/teams/{Team}.</summary>
public sealed record TeamConfig(string Sport, string League, string Team);
