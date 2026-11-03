namespace HomeTV.Core.Widgets;

/// <summary>What GET /api/widgets returns. Null or empty parts are simply not drawn.</summary>
public sealed record WidgetSnapshot(Weather? Weather, IReadOnlyList<TeamStatus> Teams);

/// <param name="Code">WMO weather code; the launcher maps it to an icon and label.</param>
/// <param name="High">Today's high, or null if the forecast didn't include it. Same for <paramref name="Low"/>.</param>
/// <param name="Hours">Hourly forecast starting at the current hour. The launcher shows the ones still ahead, so
/// data cached across an hour boundary doesn't show a past hour.</param>
/// <param name="UpdatedAt">When this data was fetched; the launcher flags it when it gets old.</param>
public sealed record Weather(
    string Place,
    string Units,
    double Temperature,
    int Code,
    bool IsDay,
    double? High,
    double? Low,
    IReadOnlyList<WeatherHour> Hours,
    DateTimeOffset UpdatedAt);

public sealed record WeatherHour(DateTimeOffset Time, double Temperature, int Code, bool IsDay, int? PrecipitationChance);

/// <param name="Color">Team color as hex without '#', from ESPN.</param>
/// <param name="Game">The team's current, next or just-finished game; null in the off-season.</param>
/// <param name="Sport">From the config ("football", "basketball", ...). The launcher names the sport when two
/// teams share a logo (CU football and basketball).</param>
/// <param name="Logo">Team logo for dark backgrounds, sized for the card; always on ESPN's image host.</param>
public sealed record TeamStatus(
    string Abbreviation,
    string Name,
    string? Color,
    string? Record,
    Game? Game,
    DateTimeOffset UpdatedAt,
    string? Sport = null,
    string? Logo = null);

public enum GameState
{
    Scheduled,
    Live,
    Final,
}

/// <param name="Detail">ESPN's short status, e.g. "Q3 5:32", "Halftime", "Final/OT".</param>
/// <param name="Broadcast">TV channel(s), e.g. "ESPN".</param>
/// <param name="Opponent">The opponent's abbreviation, e.g. "KC".</param>
/// <param name="OpponentLogo">Same rules as <see cref="TeamStatus.Logo"/>.</param>
/// <param name="OpponentRecord">e.g. "5-2". From the game when it's over, else looked up by <see cref="WidgetService"/>.</param>
/// <param name="OpponentId">ESPN team id (digits only), used to look up <paramref name="OpponentRecord"/>.</param>
public sealed record Game(
    GameState State,
    DateTimeOffset StartsAt,
    string Detail,
    bool IsHome,
    string Opponent,
    int? Score,
    int? OpponentScore,
    string? Broadcast,
    string? OpponentName = null,
    string? OpponentLogo = null,
    string? OpponentRecord = null,
    string? OpponentId = null);
