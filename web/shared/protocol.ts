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
  /** `continue`: a ContinueEntry id, to reopen that page instead of the app's start page. */
  | { type: "launch"; app: string; continue?: string }
  | { type: "home" }
  | { type: "sleep" }
  | { type: "desktop" }
  /** Seconds. */
  | { type: "seek"; position: number }
  /** Closes an open app, in front or in the background. */
  | { type: "close"; app: string }
  /** Tiles 2–4 apps side by side; fewer ends split screen with that app (or the active tile) full screen. */
  | { type: "split"; apps: string[] };

export type HubEvent =
  /** `running`: open apps, most recently used first. `split`: tiled apps in screen order, or null. */
  | { type: "state"; foregroundApp: string | null; running: string[]; split: string[] | null }
  | { type: "key"; key: RemoteKey }
  | { type: "pinShown"; pin: string; expiresAt: string }
  | { type: "pinHidden" }
  | { type: "nowPlaying"; media: NowPlaying | null }
  /** Focus on the TV moved into a text field, or out of one (null). */
  | { type: "textFocus"; field: TextField | null }
  /** Whether there's anything to scroll under the TV's pointer. */
  | { type: "scrollable"; scrollable: boolean }
  | ({ type: "audio" } & AudioState)
  /** A phone added or deleted a background photo: the launcher re-reads the list. */
  | { type: "photosChanged" };

/** Where sound can go (config/audio.json, AudioOutputService.cs). `current` is null for a device not listed there. */
export interface AudioState {
  outputs: AudioOutput[];
  current: string | null;
}

export interface AudioOutput {
  id: string;
  name: string;
  icon: "tv" | "soundbar" | "speaker";
  /** Connected, and the TV is set up if it needs to be. */
  available: boolean;
}

/** A focused text field on the TV (ITextFocus.cs). */
export interface TextField {
  /** Its label or placeholder, e.g. "Search". */
  name: string | null;
  /** A password field. */
  secret: boolean;
}

/** What Windows' media controls report (NowPlaying.cs). Positions are seconds, as of when the event was sent. */
export interface NowPlaying {
  /** e.g. "MSEdge", "Spotify.exe". */
  source: string;
  title: string | null;
  artist: string | null;
  album: string | null;
  status: "playing" | "paused" | "stopped";
  position: number | null;
  duration: number | null;
  canSeek: boolean;
  /** Artwork is at /api/now-playing/art?v=<artVersion>; null when there's none. */
  artVersion: string | null;
}

/** GET /api/continue: the "Continue watching" row, newest first (ContinueService.cs). */
export interface ContinueEntry {
  id: string;
  appId: string;
  title: string;
  /** https image from the page, or null. */
  image: string | null;
  /** Seconds; null if the page had no video position. */
  position: number | null;
  /** Seconds; null for live streams. */
  duration: number | null;
  updatedAt: string;
}

/** GET /api/system (launcher only). */
export interface SystemInfo {
  hostName: string;
  addresses: string[];
  port: number;
  version: string | null;
  /** True when a watchdog will start HomeTV again after "Restart HomeTV". */
  supervised: boolean;
}

/** Which photos the slideshow plays (PhotoSorting.cs). */
export type PhotoMode = "all" | "people" | "nature" | "safeForWork";

export const PHOTO_MODES: { mode: PhotoMode; label: string }[] = [
  { mode: "all", label: "All" },
  { mode: "people", label: "People" },
  { mode: "nature", label: "Nature" },
  { mode: "safeForWork", label: "Safe for work" },
];

/** GET /api/photos: the launcher's background slideshow. */
export interface PhotoList {
  /** Where on the box photos are kept. */
  folder: string;
  mode: PhotoMode;
  /** How long each photo stays up. Set with PUT /api/photos/settings, like the mode. */
  slideSeconds: number;
  /** The on-box image model sorting photos: how many are waiting, and any status worth showing. */
  sorting: { pending: number; status: string | null };
  photos: Photo[];
}

/** What the image model (or someone, by hand) says is in a photo. Null until it's been looked at; videos aren't. */
export interface PhotoTags {
  people: boolean;
  nature: boolean;
  safeForWork: boolean;
  /** Set by hand on a phone (PUT /api/photos/{name}/tags), so the model leaves it alone. */
  manual: boolean;
}

/** A background photo or video (PhotoLibrary.cs). Videos play muted. */
export interface Photo {
  name: string;
  url: string;
  kind: "image" | "video";
  tags: PhotoTags | null;
  /** In the current mode's slideshow. */
  shown: boolean;
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
  /** Settings offers "Sign in to <name>" (AppDefinition.SignInUrl). */
  canSignIn?: boolean;
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
