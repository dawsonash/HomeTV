// Wire format shared with HomeTV.Core (RemoteCommand.cs, HubEvent.cs). Keep the two in sync.

export type RemoteKey =
  | "up" | "down" | "left" | "right" | "ok" | "back"
  | "playPause" | "next" | "previous"
  | "volumeUp" | "volumeDown" | "mute"
  | "backspace" | "enter";

export type RemoteCommand =
  | { type: "key"; key: RemoteKey }
  | { type: "mouseMove"; dx: number; dy: number }
  | { type: "click"; button: "left" | "right" }
  | { type: "scroll"; dy: number }
  | { type: "text"; text: string }
  | { type: "launch"; app: string }
  | { type: "home" }
  | { type: "sleep" }
  | { type: "desktop" };

export type HubEvent =
  | { type: "state"; foregroundApp: string | null }
  | { type: "key"; key: RemoteKey }
  | { type: "pinShown"; pin: string; expiresAt: string }
  | { type: "pinHidden" };

/** GET /api/system (launcher only). */
export interface SystemInfo {
  hostName: string;
  addresses: string[];
  port: number;
  version: string | null;
  /** True when a watchdog will start HomeTV again after "Restart HomeTV". */
  supervised: boolean;
}

/** GET /api/devices. */
export interface PairedDeviceInfo {
  id: string;
  name: string;
  pairedAt: string;
  /** Touchpad sensitivity multiplier; set with PUT /api/devices/{id}/pointer-speed (launcher only). */
  pointerSpeed: number;
}

/** Range the server clamps pointerSpeed to (PairedDevice.cs). */
export const POINTER_SPEED_MIN = 0.2;
export const POINTER_SPEED_MAX = 3;

export interface AppTile {
  id: string;
  name: string;
  color: string | null;
}

/** GET /api/widgets (WidgetData.cs). Missing parts aren't configured or haven't loaded yet. */
export interface WidgetSnapshot {
  weather: Weather | null;
  teams: TeamStatus[];
}

export interface Weather {
  place: string;
  units: "F" | "C";
  temperature: number;
  /** WMO weather code. */
  code: number;
  isDay: boolean;
  /** Today's high/low; null if the forecast left them out. */
  high: number | null;
  low: number | null;
  /** Hourly forecast from the current hour (WidgetData.cs). */
  hours: WeatherHour[];
  updatedAt: string;
}

export interface WeatherHour {
  /** ISO time with offset. */
  time: string;
  temperature: number;
  code: number;
  isDay: boolean;
  precipitationChance: number | null;
}

export interface TeamStatus {
  abbreviation: string;
  name: string;
  /** Hex without '#'. */
  color: string | null;
  record: string | null;
  game: Game | null;
  updatedAt: string;
  /** ESPN sport slug from the config, e.g. "football". */
  sport: string | null;
  /** Logo for dark backgrounds, on ESPN's image host (checked server-side). */
  logo: string | null;
}

export interface Game {
  state: "scheduled" | "live" | "final";
  startsAt: string;
  /** e.g. "Q3 5:32", "Halftime", "Final/OT". */
  detail: string;
  isHome: boolean;
  opponent: string;
  score: number | null;
  opponentScore: number | null;
  broadcast: string | null;
  opponentName: string | null;
  /** Same rules as TeamStatus.logo. */
  opponentLogo: string | null;
  /** e.g. "5-2"; null if it couldn't be looked up. */
  opponentRecord: string | null;
  opponentId: string | null;
}
