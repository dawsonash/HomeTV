import type { Game, TeamStatus, Weather, WidgetSnapshot } from "../shared/protocol";

/** While a game is on (or about to start), so the score keeps up. Matches the server's live refresh. */
const LIVE_POLL_MS = 30_000;
/** Otherwise. Weather only changes every 15 minutes and schedules hardly at all. */
const IDLE_POLL_MS = 5 * 60_000;
/** A game starting within this long counts as live, so the first score shows up promptly. */
const IMMINENT_MS = 15 * 60_000;
/** Older than this, a card says "as of 3:40 PM" so old data isn't mistaken for current. */
const STALE_MS = 45 * 60_000;

/** Draws the weather and sports cards into `container` and keeps them up to date. */
export function startWidgets(container: HTMLElement): void {
  let live = false;
  const load = async () => {
    try {
      const response = await fetch("/api/widgets");
      if (response.ok) {
        const data = (await response.json()) as WidgetSnapshot;
        live = data.teams.some((t) => isLiveOrImminent(t.game));
        render(container, data);
      }
    } catch {
      // Server restarting; keep what's on screen and try again next poll.
    }
    window.setTimeout(load, live ? LIVE_POLL_MS : IDLE_POLL_MS);
  };
  void load();
}

function isLiveOrImminent(game: Game | null): boolean {
  if (!game) return false;
  if (game.state === "live") return true;
  return game.state === "scheduled" && new Date(game.startsAt).getTime() - Date.now() < IMMINENT_MS;
}

let lastRendered = "";

/**
 * Redraws only when something on screen would change: a score, a temperature, a status. Fetch timestamps are
 * left out of the comparison (only whether data is stale counts), and the hour is in, so the hourly forecast
 * and "Tomorrow"/"Today" labels still roll over.
 */
function render(container: HTMLElement, data: WidgetSnapshot): void {
  const visible = JSON.stringify(data, (name, value) => (name === "updatedAt" ? undefined : value));
  const stale = [data.weather?.updatedAt, ...data.teams.map((t) => t.updatedAt)].map((t) => !!t && isStale(t)).join();
  const key = visible + stale + new Date().toDateString() + new Date().getHours();
  if (key === lastRendered) return;
  lastRendered = key;

  const cards = [data.weather && weatherCard(data.weather), data.teams.length > 0 && sportsCard(data.teams)].filter(
    (card): card is HTMLElement => !!card,
  );
  container.replaceChildren(...cards);
  container.hidden = cards.length === 0;
}

// ---- Weather ----

/** How many upcoming hours the card shows. */
const HOURS_SHOWN = 6;
/** Rain chance below this is left off the hourly strip, so it only shows up when it matters. */
const RAIN_SHOWN_FROM = 20;

function weatherCard(weather: Weather): HTMLElement {
  const { label, icon } = describe(weather.code, weather.isDay);

  const card = el("section", "widget weather");
  const now = el("div", "wx-now");
  now.append(
    el("span", "wx-icon", icon),
    el("span", "wx-temp", `${Math.round(weather.temperature)}°`),
  );
  const highLow = weather.high !== null && weather.low !== null ? `H ${Math.round(weather.high)}° · L ${Math.round(weather.low)}°` : null;
  const info = el("div", "wx-info");
  info.append(el("div", "wx-label", label), el("div", "muted", [weather.place, highLow].filter(Boolean).join(" · ")));
  now.append(info);

  // Hours still ahead: the data can be up to 15 minutes old, so the first entry may already be the current hour.
  const upcoming = weather.hours.filter((h) => new Date(h.time).getTime() > Date.now()).slice(0, HOURS_SHOWN);
  const hours = el("div", "wx-hours");
  for (const hour of upcoming) {
    const cell = el("div", "wx-hour");
    cell.append(
      el("span", "muted", new Date(hour.time).toLocaleTimeString([], { hour: "numeric" })),
      el("span", "wx-hour-icon", describe(hour.code, hour.isDay).icon),
      el("span", "", `${Math.round(hour.temperature)}°`),
      el("span", "muted wx-rain", (hour.precipitationChance ?? 0) >= RAIN_SHOWN_FROM ? `${hour.precipitationChance}%` : " "),
    );
    hours.append(cell);
  }

  card.append(now);
  if (upcoming.length > 0) card.append(hours);
  card.append(footer("", weather.updatedAt));
  return card;
}

/** WMO weather interpretation codes, as used by Open-Meteo. */
function describe(code: number, isDay: boolean): { label: string; icon: string } {
  if (code === 0) return { label: "Clear", icon: isDay ? "☀️" : "🌙" };
  if (code === 1) return { label: "Mostly clear", icon: isDay ? "🌤️" : "🌙" };
  if (code === 2) return { label: "Partly cloudy", icon: isDay ? "⛅" : "☁️" };
  if (code === 3) return { label: "Cloudy", icon: "☁️" };
  if (code === 45 || code === 48) return { label: "Fog", icon: "🌫️" };
  if (code >= 51 && code <= 55) return { label: "Drizzle", icon: "🌦️" };
  if (code === 56 || code === 57) return { label: "Freezing drizzle", icon: "🌧️" };
  if (code === 61) return { label: "Light rain", icon: "🌧️" };
  if (code === 63) return { label: "Rain", icon: "🌧️" };
  if (code === 65) return { label: "Heavy rain", icon: "🌧️" };
  if (code === 66 || code === 67) return { label: "Freezing rain", icon: "🌧️" };
  if ((code >= 71 && code <= 77) || code === 85 || code === 86) return { label: "Snow", icon: "❄️" };
  if (code >= 80 && code <= 82) return { label: "Showers", icon: "🌦️" };
  if (code >= 95) return { label: "Thunderstorms", icon: "⛈️" };
  return { label: "—", icon: "🌡️" };
}

// ---- Sports ----

function sportsCard(teams: TeamStatus[]): HTMLElement {
  const card = el("section", "widget sports");
  const list = el("div", "games");
  const logoCounts = new Map<string, number>();
  for (const t of teams) if (t.logo) logoCounts.set(t.logo, (logoCounts.get(t.logo) ?? 0) + 1);
  for (const team of teams) list.append(gameBlock(team, !!team.logo && (logoCounts.get(team.logo) ?? 0) > 1));
  card.append(list, footer("", oldest(teams.map((t) => t.updatedAt))));
  return card;
}

const SPORT_NAMES: Record<string, string> = {
  football: "Football",
  basketball: "Basketball",
  baseball: "Baseball",
  hockey: "Hockey",
  soccer: "Soccer",
};

interface Side {
  abbreviation: string;
  name: string;
  logo: string | null;
  color: string | null;
  record: string | null;
  score: number | null;
  ours: boolean;
}

/**
 * One game, scoreboard style: away team over home team, each with logo, abbreviation, record and score, and the
 * status (time and channel, live clock, or Final) on the right. Our team is bold; after the game the loser dims.
 */
function gameBlock(team: TeamStatus, sharesLogo: boolean): HTMLElement {
  const game = team.game;
  const block = el("div", "game");
  const us: Side = {
    abbreviation: team.abbreviation,
    name: team.name,
    logo: team.logo,
    color: team.color,
    record: team.record,
    score: game?.score ?? null,
    ours: true,
  };

  const sides = el("div", "game-sides");
  const status = el("div", "game-status");
  // Two teams with the same logo (e.g. a school's football and basketball) are told apart by naming the sport.
  const sport = sharesLogo && team.sport ? SPORT_NAMES[team.sport] ?? team.sport : null;

  if (!game) {
    sides.append(sideRow(us, null));
    status.append(el("div", "muted", "No upcoming games"));
    if (sport) status.append(el("div", "muted small", sport));
    block.append(sides, status);
    return block;
  }

  const them: Side = {
    abbreviation: game.opponent,
    name: game.opponentName ?? game.opponent,
    logo: game.opponentLogo,
    color: null,
    record: game.opponentRecord,
    score: game.opponentScore,
    ours: false,
  };
  const [away, home] = game.isHome ? [them, us] : [us, them];
  const decided = game.state === "final" && us.score !== null && them.score !== null && us.score !== them.score;
  const winner = decided ? ((us.score ?? 0) > (them.score ?? 0) ? us : them) : null;
  sides.append(sideRow(away, winner), sideRow(home, winner));

  const start = new Date(game.startsAt);
  if (game.state === "live") {
    block.classList.add("live");
    status.append(el("div", "live-dot", "● LIVE"), el("div", "", game.detail));
  } else if (game.state === "final") {
    status.append(el("div", "", game.detail || "Final"), el("div", "muted", dayLabel(start)));
  } else {
    status.append(
      el("div", "", dayLabel(start)),
      el("div", "muted", start.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })),
    );
    if (game.broadcast) status.append(el("div", "muted small", game.broadcast));
  }
  if (sport) status.append(el("div", "muted small", sport));

  block.append(sides, status);
  return block;
}

function sideRow(side: Side, winner: Side | null): HTMLElement {
  const row = el("div", "side");
  if (side.ours) row.classList.add("ours");
  if (winner && winner !== side) row.classList.add("lost");
  row.append(
    logoOrBadge(side),
    el("span", "side-abbr", side.abbreviation),
    el("span", "side-record muted", side.record ?? ""),
    el("span", "side-score", side.score === null ? "" : String(side.score)),
  );
  return row;
}

function logoOrBadge(side: Side): HTMLElement {
  if (!side.logo) return badge(side);
  const img = document.createElement("img");
  img.className = "team-logo";
  img.src = side.logo;
  img.alt = side.name;
  // Offline or blocked: fall back to the colored abbreviation badge.
  img.addEventListener("error", () => img.replaceWith(badge(side)), { once: true });
  return img;
}

/** Colored abbreviation, used when a team has no logo. */
function badge(side: Side): HTMLElement {
  const node = el("span", "team-badge", side.abbreviation);
  if (side.color && /^[0-9a-f]{6}$/i.test(side.color)) {
    node.style.setProperty("--team", `#${side.color}`);
    // Light team colors (CU gold) need dark text to stay readable.
    if (luminance(side.color) > 0.5) node.classList.add("light");
  }
  node.title = side.name;
  return node;
}

/** Rough perceived brightness of a hex color, 0–1. */
function luminance(hex: string): number {
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  return 0.299 * r + 0.587 * g + 0.114 * b;
}

/** "Today", "Tomorrow", "Yesterday", "Sat", or "Nov 12" further out. */
function dayLabel(date: Date): string {
  const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const days = Math.round((startOfDay(date) - startOfDay(new Date())) / 86_400_000);
  if (days === 0) return "Today";
  if (days === 1) return "Tomorrow";
  if (days === -1) return "Yesterday";
  if (days > 1 && days < 7) return date.toLocaleDateString([], { weekday: "short" });
  return date.toLocaleDateString([], { month: "short", day: "numeric" });
}

// ---- Shared ----

function footer(credit: string, updatedAt: string | null): HTMLElement {
  const parts = [credit];
  if (updatedAt && isStale(updatedAt)) {
    parts.push(`as of ${new Date(updatedAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}`);
  }
  return el("div", "widget-footer muted", parts.filter(Boolean).join(" · "));
}

function isStale(updatedAt: string): boolean {
  return Date.now() - new Date(updatedAt).getTime() > STALE_MS;
}

function oldest(dates: string[]): string | null {
  return dates.reduce<string | null>((a, b) => (a === null || new Date(b) < new Date(a) ? b : a), null);
}

/** Text goes in via textContent: team names and statuses come from a third-party API. */
function el(tag: string, className: string, text?: string): HTMLElement {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}
