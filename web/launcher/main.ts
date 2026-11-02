import type { AppTile, PairedDeviceInfo, RemoteKey, SystemInfo } from "../shared/protocol";
import { HubSocket } from "../shared/socket";

const $ = <T extends HTMLElement>(selector: string) => document.querySelector<T>(selector)!;

/** Pseudo-app id of the Settings tile at the end of the row. */
const SETTINGS = "__settings";
/** How long a "press OK again to confirm" stays armed. */
const CONFIRM_MS = 4000;

let apps: AppTile[] = [];
let focusedTile = 0;
let focusedSetting = 0;
let view: "home" | "settings" = "home";
let system: SystemInfo | null = null;
let pinTimer: number | undefined;
let toastTimer: number | undefined;
let armed: { button: HTMLButtonElement; label: string; timer: number } | null = null;

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
        // A phone may have just paired.
        if (view === "settings") void loadDevices();
        break;
    }
  },
});

// ---- Input ----

function handleKey(key: RemoteKey): void {
  if (!$("#pin-overlay").hidden) {
    if (key === "back") hidePin();
    return;
  }
  if (view === "settings") settingsKey(key);
  else homeKey(key);
}

function homeKey(key: RemoteKey): void {
  switch (key) {
    case "left":
      moveTile(-1);
      break;
    case "right":
      moveTile(1);
      break;
    case "ok":
    case "enter":
      activateTile();
      break;
  }
}

function settingsKey(key: RemoteKey): void {
  switch (key) {
    case "up":
    case "left":
      moveSetting(-1);
      break;
    case "down":
    case "right":
      moveSetting(1);
      break;
    case "ok":
    case "enter":
      focusables()[focusedSetting]?.click();
      break;
    case "back":
      showView("home");
      break;
  }
}

// A keyboard or controller plugged into the box works too.
document.addEventListener("keydown", (event) => {
  const keys: Record<string, RemoteKey> = {
    ArrowLeft: "left",
    ArrowRight: "right",
    ArrowUp: "up",
    ArrowDown: "down",
    Enter: "ok",
    Escape: "back",
    Backspace: "back",
  };
  const key = keys[event.key];
  if (key) {
    event.preventDefault();
    handleKey(key);
  }
});

// ---- Home ----

function tileIds(): string[] {
  return [...apps.map((a) => a.id), SETTINGS];
}

function moveTile(by: number): void {
  focusedTile = Math.max(0, Math.min(tileIds().length - 1, focusedTile + by));
  renderFocus();
}

function activateTile(): void {
  const id = tileIds()[focusedTile];
  if (id === SETTINGS) showView("settings");
  else if (id) socket.send({ type: "launch", app: id });
}

function renderTiles(): void {
  const tiles = apps.map((app) => {
    const tile = document.createElement("div");
    tile.className = "tile";
    tile.role = "listitem";
    tile.textContent = app.name;
    if (app.color) tile.style.setProperty("--tile-color", app.color);
    return tile;
  });

  const settings = document.createElement("div");
  settings.className = "tile tile-settings";
  settings.role = "listitem";
  settings.innerHTML = `<span class="gear" aria-hidden="true">⚙</span>Settings`;

  const all = [...tiles, settings];
  all.forEach((tile, i) => {
    // Pointing selects, clicking opens: same as the D-pad.
    tile.addEventListener("mouseenter", () => {
      focusedTile = i;
      renderFocus();
    });
    tile.addEventListener("click", () => {
      focusedTile = i;
      renderFocus();
      activateTile();
    });
  });

  $("#tiles").replaceChildren(...all);
  renderFocus();
}

function renderFocus(): void {
  document.querySelectorAll(".tile").forEach((tile, i) => tile.classList.toggle("focused", i === focusedTile));
}

// In production the app's own window covers the launcher; this shows while it starts.
function showOpening(appId: string | null): void {
  const app = apps.find((a) => a.id === appId);
  $("#opening").hidden = !app;
  if (app) $("#opening-name").textContent = app.name;
}

// ---- Settings ----

function showView(next: "home" | "settings"): void {
  view = next;
  disarm();
  $("#home").hidden = next !== "home";
  $("#settings").hidden = next !== "settings";
  if (next === "settings") {
    focusedSetting = 0;
    renderSettingFocus();
    void loadSystem();
    void loadDevices();
  }
}

// Pointing at a settings button selects it, so the mouse and D-pad never disagree about focus.
$("#settings").addEventListener("mouseover", (event) => {
  const target = (event.target as HTMLElement).closest<HTMLButtonElement>("[data-focusable]");
  const index = target ? focusables().indexOf(target) : -1;
  if (index >= 0 && index !== focusedSetting) {
    disarm();
    focusedSetting = index;
    renderSettingFocus();
  }
});

$("#settings-back").addEventListener("click", () => showView("home"));
$("#pin-overlay").addEventListener("click", hidePin);

// The pointer stays hidden on the TV until the mouse (or the phone's touchpad) actually moves.
let pointerTimer: number | undefined;
document.addEventListener("mousemove", () => {
  document.body.classList.add("pointer");
  clearTimeout(pointerTimer);
  pointerTimer = window.setTimeout(() => document.body.classList.remove("pointer"), 3000);
});

function focusables(): HTMLButtonElement[] {
  return [...document.querySelectorAll<HTMLButtonElement>("#settings [data-focusable]")];
}

function moveSetting(by: number): void {
  const items = focusables();
  if (items.length === 0) return;
  disarm();
  focusedSetting = Math.max(0, Math.min(items.length - 1, focusedSetting + by));
  renderSettingFocus();
}

function renderSettingFocus(): void {
  const items = focusables();
  focusedSetting = Math.min(focusedSetting, Math.max(0, items.length - 1));
  items.forEach((item, i) => item.classList.toggle("focused", i === focusedSetting));
  items[focusedSetting]?.scrollIntoView({ block: "nearest" });
}

/** Runs `action` on the second OK within a few seconds; the first press only arms the button. */
function confirmThen(button: HTMLButtonElement, action: () => void): void {
  if (armed?.button === button) {
    disarm();
    action();
    return;
  }
  disarm();
  armed = { button, label: button.textContent ?? "", timer: window.setTimeout(disarm, CONFIRM_MS) };
  button.textContent = `${armed.label}: press OK again`;
  button.classList.add("armed");
}

function disarm(): void {
  if (!armed) return;
  clearTimeout(armed.timer);
  armed.button.textContent = armed.label;
  armed.button.classList.remove("armed");
  armed = null;
}

function button(id: string, action: (button: HTMLButtonElement) => void): void {
  const el = $<HTMLButtonElement>(`#${id}`);
  el.addEventListener("click", () => (el.hasAttribute("data-confirm") ? confirmThen(el, () => action(el)) : action(el)));
}

button("pair", async () => {
  const response = await post("/api/pair/request");
  if (response.status === 429) {
    const { retryAfterSeconds } = (await response.json()) as { retryAfterSeconds: number };
    toast(`Wait ${retryAfterSeconds}s before asking for another PIN`);
  } else if (!response.ok) {
    toast("Couldn't show a PIN");
  } else {
    toast("On your phone, open the address above and enter the PIN");
  }
});
button("sleep", () => socket.send({ type: "sleep" }));
button("desktop", () => socket.send({ type: "desktop" }));
button("restart-pc", async () => {
  toast("Restarting the PC…");
  await post("/api/system/restart-pc");
});
button("restart-app", async () => {
  toast(system?.supervised === false ? "HomeTV is stopping (no watchdog in dev, so it won't come back)" : "Restarting HomeTV…");
  await post("/api/system/restart-app");
});

async function loadSystem(): Promise<void> {
  const response = await fetch("/api/system");
  if (!response.ok) return;
  system = (await response.json()) as SystemInfo;

  const address = system.addresses[0] ?? `${system.hostName.toLowerCase()}.local`;
  const url = `http://${address}:${system.port}`;
  document.querySelectorAll(".remote-url").forEach((el) => (el.textContent = url));
  $("#remote-alt").textContent = system.addresses.length
    ? `or http://${system.hostName.toLowerCase()}.local:${system.port}`
    : "";
  $("#about").textContent = [`PC name: ${system.hostName}`, system.version && `HomeTV ${system.version}`, system.supervised ? "Watchdog on" : "Watchdog off"]
    .filter(Boolean)
    .join(" · ");
}

async function loadDevices(): Promise<void> {
  const response = await fetch("/api/devices");
  if (!response.ok) return;
  const devices = (await response.json()) as PairedDeviceInfo[];

  const list = $("#devices");
  if (devices.length === 0) {
    const empty = document.createElement("li");
    empty.className = "muted";
    empty.textContent = "No phones paired yet.";
    list.replaceChildren(empty);
  } else {
    list.replaceChildren(
      ...devices.map((device) => {
        const row = document.createElement("li");
        const label = document.createElement("span");
        label.textContent = device.name || "Phone";
        const since = document.createElement("span");
        since.className = "muted";
        since.textContent = `paired ${new Date(device.pairedAt).toLocaleDateString()}`;

        const remove = document.createElement("button");
        remove.dataset.focusable = "";
        remove.textContent = "Remove";
        remove.addEventListener("click", () =>
          confirmThen(remove, async () => {
            const result = await fetch(`/api/devices/${encodeURIComponent(device.id)}`, { method: "DELETE" });
            toast(result.ok ? `Removed ${label.textContent}` : "Couldn't remove it");
            await loadDevices();
          }),
        );

        row.append(label, since, remove);
        return row;
      }),
    );
  }
  renderSettingFocus();
}

function post(path: string): Promise<Response> {
  return fetch(path, { method: "POST" });
}

function toast(message: string): void {
  const el = $("#toast");
  el.textContent = message;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => (el.hidden = true), 5000);
}

// ---- PIN ----

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

// ---- Startup ----

function tickClock(): void {
  $("#clock").textContent = new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
}

tickClock();
setInterval(tickClock, 10_000);
void loadSystem();

fetch("/api/apps")
  .then((r) => r.json() as Promise<AppTile[]>)
  .then((loaded) => {
    apps = loaded;
    renderTiles();
  });
