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

export interface AppTile {
  id: string;
  name: string;
  color: string | null;
}
