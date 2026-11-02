import type { AppTile, RemoteKey } from "../shared/protocol";
import { HubSocket } from "../shared/socket";

const $ = <T extends HTMLElement>(selector: string) => document.querySelector<T>(selector)!;

let apps: AppTile[] = [];
let focused = 0;
let pinTimer: number | undefined;

const socket = new HubSocket({
  path: "/ws/launcher",
  onStatus: (online) => ($("#offline").hidden = online),
  onEvent: (event) => {
    switch (event.type) {
      case "key":
        handleKey(event.key);
        break;
      case "state":
        showOpening(event.foregroundApp);
        break;
      case "pinShown":
        showPin(event.pin, new Date(event.expiresAt));
        break;
      case "pinHidden":
        hidePin();
        break;
    }
  },
});

function handleKey(key: RemoteKey): void {
  switch (key) {
    case "left":
      moveFocus(-1);
      break;
    case "right":
      moveFocus(1);
      break;
    case "ok":
    case "enter":
      if (apps[focused]) socket.send({ type: "launch", app: apps[focused].id });
      break;
    case "back":
      hidePin();
      break;
  }
}

function moveFocus(by: number): void {
  if (apps.length === 0) return;
  focused = Math.max(0, Math.min(apps.length - 1, focused + by));
  renderFocus();
}

function renderTiles(): void {
  $("#tiles").replaceChildren(
    ...apps.map((app) => {
      const tile = document.createElement("div");
      tile.className = "tile";
      tile.role = "listitem";
      tile.textContent = app.name;
      if (app.color) tile.style.setProperty("--tile-color", app.color);
      return tile;
    }),
  );
  renderFocus();
}

function renderFocus(): void {
  document.querySelectorAll(".tile").forEach((tile, i) => tile.classList.toggle("focused", i === focused));
}

// In production the app's own window covers the launcher; this shows while it starts (and on the Mac, where nothing launches).
function showOpening(appId: string | null): void {
  const app = apps.find((a) => a.id === appId);
  $("#opening").hidden = !app;
  if (app) $("#opening-name").textContent = app.name;
}

function showPin(pin: string, expiresAt: Date): void {
  $("#pin").textContent = pin;
  $("#pin-overlay").hidden = false;
  clearInterval(pinTimer);
  const tick = () => {
    const seconds = Math.max(0, Math.round((expiresAt.getTime() - Date.now()) / 1000));
    $("#pin-expiry").textContent = `Expires in ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
    if (seconds === 0) hidePin();
  };
  tick();
  pinTimer = window.setInterval(tick, 1000);
}

function hidePin(): void {
  clearInterval(pinTimer);
  $("#pin-overlay").hidden = true;
}

function tickClock(): void {
  $("#clock").textContent = new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
}

// A keyboard or controller plugged into the box works too.
document.addEventListener("keydown", (event) => {
  const keys: Record<string, RemoteKey> = { ArrowLeft: "left", ArrowRight: "right", Enter: "ok", Escape: "back" };
  const key = keys[event.key];
  if (key) {
    event.preventDefault();
    handleKey(key);
  }
});

$("#remote-url").textContent = `http://hometv.local:${location.port || 80}`;
tickClock();
setInterval(tickClock, 10_000);

fetch("/api/apps")
  .then((r) => r.json() as Promise<AppTile[]>)
  .then((loaded) => {
    apps = loaded;
    renderTiles();
  });
