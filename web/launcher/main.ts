import { PHOTO_MODES, POINTER_SPEED_MAX, POINTER_SPEED_MIN, type AppTile, type ContinueEntry, type PairedDeviceInfo, type PhotoList, type PhotoMode, type RemoteKey, type SystemInfo } from "../shared/protocol";
import { cycleAudio, renderAudio } from "./audio";
import { fetchContinue, removeContinue, renderContinue } from "./continue";
import { qrSvg } from "../shared/qr";
import { HubSocket } from "../shared/socket";
import { startSlideshow } from "./slideshow";
import { startWidgets } from "./widgets";

const $ = <T extends HTMLElement>(selector: string) => document.querySelector<T>(selector)!;

/** How long a "press OK again to confirm" stays armed. */
const CONFIRM_MS = 4000;
/** No remote, keyboard or mouse input for this long and the home screen goes ambient: just photos, clock and widgets. */
const IDLE_MS = 5 * 60_000;

let apps: AppTile[] = [];
let focusedTile = 0;
/** Tiles per row on the home screen; keep in step with `.tiles` in styles.css. */
const TILES_PER_ROW = 4;
/** "Continue watching" cards, and whether the D-pad is in that row (below the tiles) rather than on the tiles. */
let continueEntries: ContinueEntry[] = [];
let inContinueRow = false;
let focusedCard = 0;
/** The D-pad is on the focused card's ✕ (up from the card) rather than the card itself. */
let onRemoveButton = false;
/** A header button is selected instead of a tile: the audio button (top left), or idle or settings (top right). */
type HeaderButton = "audio" | "idle" | "gear";
let headerFocus: HeaderButton | null = null;
let focusedSetting = 0;
let view: "home" | "settings" = "home";
let system: SystemInfo | null = null;
/** The address phones use, e.g. http://10.0.0.31:8888. Null until /api/system answers. */
let remoteUrl: string | null = null;
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
        showOpening(event.split ? null : event.foregroundApp);
        // Behind split-screen tiles: plain black, so any sliver the tiles leave reads as letterboxing.
        document.body.classList.toggle("split", !!event.split);
        // A background video doesn't need to play behind an app.
        slideshow.setPaused(event.foregroundApp !== null || !!event.split);
        // Back home: what was just watched is now first in the row.
        if (event.foregroundApp === null) void loadContinue();
        // Launched or closed an app (from the phone's app list, say): either way, someone's watching.
        wake();
        break;
      case "audio":
        renderAudio($("#audio-button"), event);
        // A device that disappears (Bluetooth speaker off) mustn't strand the D-pad on a hidden button.
        if (headerFocus === "audio" && !audioShown()) setHeaderFocus(null);
        break;
      case "photosChanged":
        slideshow.reload();
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

/**
 * Pointer first: the phone's touchpad drives the TV, so nothing shows as selected except what's under the pointer.
 * Arrow keys or OK (a keyboard or controller on the box) bring the selection ring back until the pointer moves.
 */
let usingKeys = false;
const NAV_KEYS: ReadonlySet<RemoteKey> = new Set<RemoteKey>(["up", "down", "left", "right", "ok", "enter"]);

/** Without keys, drops the ring from anything the pointer isn't on. Runs after each focus render. */
function trimFocusToPointer(): void {
  if (usingKeys) return;
  document.querySelectorAll(".focused, .remove-focused").forEach((el) => {
    if (!el.matches(":hover")) el.classList.remove("focused", "remove-focused");
  });
}

function handleKey(key: RemoteKey): void {
  if (NAV_KEYS.has(key) && !usingKeys) {
    usingKeys = true;
    if (view === "settings") renderSettingFocus();
    else renderFocus();
  }
  // The first press only brings the tiles back, so a stray OK doesn't launch whatever happens to be selected.
  if (wake()) return;
  if (!$("#pin-overlay").hidden) {
    if (key === "back") hidePin();
    return;
  }
  if (view === "settings") settingsKey(key);
  else homeKey(key);
}

function homeKey(key: RemoteKey): void {
  // Up from the tiles selects the nearer header button; left/right moves along them, down goes back.
  if (headerFocus) {
    const order: HeaderButton[] = audioShown() ? ["audio", "idle", "gear"] : ["idle", "gear"];
    const at = order.indexOf(headerFocus);
    if (key === "down" || key === "back") setHeaderFocus(null);
    else if (key === "left" && at > 0) setHeaderFocus(order[at - 1]);
    else if (key === "right" && at < order.length - 1) setHeaderFocus(order[at + 1]);
    else if (key === "ok" || key === "enter") {
      if (headerFocus === "gear") showView("settings");
      else if (headerFocus === "idle") goAmbient(true);
      else void cycleAudio($("#audio-button"), toast);
    }
    return;
  }
  if (inContinueRow) {
    continueKey(key);
    return;
  }
  switch (key) {
    case "down":
      if (tileRow(focusedTile) < tileRow(apps.length - 1)) {
        // The tile below, or the last tile when the row below is shorter.
        focusedTile = Math.min(apps.length - 1, focusedTile + TILES_PER_ROW);
        renderFocus();
      }
      break;
    case "left":
      moveTile(-1);
      break;
    case "right":
      moveTile(1);
      break;
    case "up":
      if (tileRow(focusedTile) > 0) {
        focusedTile -= TILES_PER_ROW;
        renderFocus();
      } else if (continueEntries.length > 0) {
        // Continue watching sits above the tiles, top right: start on its bottom card.
        inContinueRow = true;
        onRemoveButton = false;
        focusedCard = continueEntries.length - 1;
        renderFocus();
      } else {
        setHeaderFocus(audioShown() && focusedTile % TILES_PER_ROW < TILES_PER_ROW / 2 ? "audio" : "gear");
      }
      break;
    case "ok":
    case "enter":
      activateTile();
      break;
  }
}

function continueKey(key: RemoteKey): void {
  switch (key) {
    case "left":
    case "right":
      focusedCard = Math.max(0, Math.min(continueEntries.length - 1, focusedCard + (key === "left" ? -1 : 1)));
      renderFocus();
      break;
    case "up":
      // Card → its ✕ → the card above (one column) → the header.
      if (!onRemoveButton) onRemoveButton = true;
      else if (focusedCard >= 1) {
        focusedCard -= 1;
        onRemoveButton = false;
      } else {
        onRemoveButton = false;
        inContinueRow = false;
        headerFocus = "gear";
      }
      renderFocus();
      break;
    case "down":
      // ✕ → its card → the card below → the tiles.
      if (onRemoveButton) onRemoveButton = false;
      else if (focusedCard + 1 < continueEntries.length) focusedCard += 1;
      else inContinueRow = false;
      renderFocus();
      break;
    case "back":
      if (onRemoveButton) onRemoveButton = false;
      else inContinueRow = false;
      renderFocus();
      break;
    case "ok":
    case "enter":
      if (onRemoveButton) void removeCard(focusedCard);
      else openCard(focusedCard);
      break;
  }
}

async function removeCard(index: number): Promise<void> {
  const entry = continueEntries[index];
  if (!entry) return;
  onRemoveButton = false;
  if (!(await removeContinue(entry.id))) {
    toast("Couldn't remove that one. Try again.");
    return;
  }
  await loadContinue();
}

function openCard(index: number): void {
  const entry = continueEntries[index];
  if (entry) socket.send({ type: "launch", app: entry.appId, continue: entry.id });
}

async function loadContinue(): Promise<void> {
  continueEntries = await fetchContinue();
  if (continueEntries.length === 0) inContinueRow = false;
  focusedCard = Math.min(focusedCard, Math.max(0, continueEntries.length - 1));
  renderContinue(
    $("#continue"),
    $("#continue-row"),
    continueEntries,
    apps,
    (i) => {
      inContinueRow = true;
      onRemoveButton = false;
      headerFocus = null;
      focusedCard = i;
      renderFocus();
    },
    (i) => {
      focusedCard = i;
      renderFocus();
      openCard(i);
    },
    (i) => {
      focusedCard = i;
      void removeCard(i);
    },
  );
  renderFocus();
}

function setHeaderFocus(focus: HeaderButton | null): void {
  headerFocus = focus;
  renderFocus();
}

function audioShown(): boolean {
  return !$("#audio-button").hidden;
}

function settingsKey(key: RemoteKey): void {
  // Left/right adjust a focused slider instead of moving focus.
  const focused = focusables()[focusedSetting];
  if (focused instanceof HTMLInputElement && focused.type === "range" && (key === "left" || key === "right")) {
    stepSlider(focused, key === "left" ? -1 : 1);
    return;
  }
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

function tileRow(index: number): number {
  return Math.floor(index / TILES_PER_ROW);
}

function moveTile(by: number): void {
  focusedTile = Math.max(0, Math.min(apps.length - 1, focusedTile + by));
  renderFocus();
}

function activateTile(): void {
  const id = apps[focusedTile]?.id;
  if (id) socket.send({ type: "launch", app: id });
}

function renderTiles(): void {
  const all = apps.map((app) => {
    const tile = document.createElement("div");
    tile.className = "tile";
    tile.role = "listitem";
    tile.textContent = app.name;
    if (app.color) tile.style.setProperty("--tile-color", app.color);
    return tile;
  });

  all.forEach((tile, i) => {
    // Pointing selects, clicking opens: same as the D-pad.
    tile.addEventListener("mouseenter", () => {
      focusedTile = i;
      headerFocus = null;
      inContinueRow = false;
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
  const onTiles = !headerFocus && !inContinueRow;
  document.querySelectorAll(".tile").forEach((tile, i) => tile.classList.toggle("focused", onTiles && i === focusedTile));
  document.querySelectorAll(".card").forEach((card, i) => {
    const focused = !headerFocus && inContinueRow && i === focusedCard;
    card.classList.toggle("focused", focused);
    card.classList.toggle("remove-focused", focused && onRemoveButton);
  });
  $("#settings-button").classList.toggle("focused", headerFocus === "gear");
  $("#idle-button").classList.toggle("focused", headerFocus === "idle");
  $("#audio-button").classList.toggle("focused", headerFocus === "audio");
  trimFocusToPointer();
}

$("#settings-button").addEventListener("mouseenter", () => setHeaderFocus("gear"));
$("#audio-button").addEventListener("mouseenter", () => setHeaderFocus("audio"));
$("#audio-button").addEventListener("click", () => void cycleAudio($("#audio-button"), toast));
$("#settings-button").addEventListener("click", () => showView("settings"));
$("#idle-button").addEventListener("mouseenter", () => setHeaderFocus("idle"));
$("#idle-button").addEventListener("click", () => goAmbient(true));

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
  // The corner buttons only belong on the home screen; Settings has its own back button.
  $("#settings-button").hidden = next !== "home";
  $("#idle-button").hidden = next !== "home";
  document.body.classList.toggle("in-settings", next === "settings");
  headerFocus = null;
  renderFocus();
  if (next === "settings") {
    focusedSetting = 0;
    renderSettingFocus();
    slideshow.reload(); // Fresh mode, timer and counts for the Background photos section.
    void loadSystem();
    void loadDevices();
  }
}

// Pointing at a settings button selects it, so the mouse and D-pad never disagree about focus.
$("#settings").addEventListener("mouseover", (event) => {
  const target = (event.target as HTMLElement).closest<HTMLElement>("[data-focusable]");
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
let lastPointer = "";
document.addEventListener("mousemove", (event) => {
  // Chromium sends a mousemove without any movement when the element under a still cursor changes (as tiles fade
  // out going ambient), so only real movement counts.
  const at = `${event.screenX},${event.screenY}`;
  if (at === lastPointer) return;
  lastPointer = at;
  wake();
  if (usingKeys) {
    usingKeys = false;
    trimFocusToPointer();
  }
  document.body.classList.add("pointer");
  clearTimeout(pointerTimer);
  pointerTimer = window.setTimeout(() => document.body.classList.remove("pointer"), 3000);
});

// Leaving something takes its ring with it (entering one already selects it, via its mouseenter).
document.addEventListener("mouseout", () => {
  if (!usingKeys) requestAnimationFrame(trimFocusToPointer);
});

function focusables(): HTMLElement[] {
  return [...document.querySelectorAll<HTMLElement>("#settings [data-focusable]")];
}

function stepSlider(slider: HTMLInputElement, direction: -1 | 1): void {
  if (direction < 0) slider.stepDown();
  else slider.stepUp();
  slider.dispatchEvent(new Event("input"));
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
  if (usingKeys) items[focusedSetting]?.scrollIntoView({ block: "nearest" });
  trimFocusToPointer();
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
  if (url !== remoteUrl) {
    remoteUrl = url;
    document.querySelectorAll("[data-qr=remote]").forEach((el) => el.replaceChildren(qrSvg(`${url}/`)));
  }
  $("#remote-alt").textContent = system.addresses.length
    ? `or http://${system.hostName.toLowerCase()}.local:${system.port}`
    : "";
  $("#about").textContent = [`PC name: ${system.hostName}`, system.version && `HomeTV ${system.version}`, system.supervised ? "Watchdog on" : "Watchdog off"]
    .filter(Boolean)
    .join(" · ");
  // Open-Meteo's free tier (CC BY 4.0) asks for credit; it lives here rather than on the home screen.
  $("#credits").textContent = "Weather data by Open-Meteo.com";
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

        const info = document.createElement("div");
        info.className = "device-info";
        info.append(label, since);

        row.append(info, speedControl(device), remove);
        return row;
      }),
    );
  }
  renderSettingFocus();
}

/** "Touchpad speed" slider for one phone. Saves shortly after the last change, so holding a key sends one request. */
function speedControl(device: PairedDeviceInfo): HTMLElement {
  const wrap = document.createElement("label");
  wrap.className = "speed";

  const caption = document.createElement("span");
  caption.className = "muted";
  caption.textContent = "Touchpad speed";

  const slider = document.createElement("input");
  slider.type = "range";
  slider.dataset.focusable = "";
  slider.min = String(POINTER_SPEED_MIN);
  slider.max = String(POINTER_SPEED_MAX);
  slider.step = "0.1";
  slider.value = String(device.pointerSpeed ?? 1);
  slider.setAttribute("aria-label", `Touchpad speed for ${device.name || "Phone"}`);

  const value = document.createElement("span");
  value.className = "speed-value";
  const show = () => (value.textContent = `${Number(slider.value).toFixed(1)}×`);
  show();

  let saveTimer: number | undefined;
  slider.addEventListener("input", () => {
    show();
    clearTimeout(saveTimer);
    saveTimer = window.setTimeout(async () => {
      const response = await fetch(`/api/devices/${encodeURIComponent(device.id)}/pointer-speed`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ pointerSpeed: Number(slider.value) }),
      });
      if (!response.ok) toast("Couldn't save the touchpad speed");
    }, 300);
  });

  wrap.append(caption, slider, value);
  return wrap;
}

/** Settings → Accounts: "Sign in to YouTube" for each app with a sign-in page. */
function renderSignIn(): void {
  const signIn = apps.filter((a) => a.canSignIn);
  $("#accounts").hidden = signIn.length === 0;
  $("#sign-in-buttons").replaceChildren(
    ...signIn.map((app) => {
      const el = document.createElement("button");
      el.dataset.focusable = "";
      el.textContent = `Sign in to ${app.name}`;
      el.addEventListener("click", async () => {
        toast(`Opening ${app.name} sign-in…`);
        const response = await post(`/api/system/sign-in/${encodeURIComponent(app.id)}`);
        if (!response.ok) toast(`Couldn't open ${app.name} sign-in`);
      });
      return el;
    }),
  );
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

// ---- Ambient (idle) ----

let idleTimer: number | undefined;

/** Restarts the idle countdown. Returns true if the screen was ambient, i.e. this input only woke it up. */
function wake(): boolean {
  const wasAmbient = document.body.classList.contains("ambient");
  if (wasAmbient) setAmbient(false);
  clearTimeout(idleTimer);
  idleTimer = window.setTimeout(() => goAmbient(false), IDLE_MS);
  return wasAmbient;
}

/** Ambient: after IDLE_MS with nothing pressed, or straight away from the Idle button (`now`). */
function goAmbient(now: boolean): void {
  // A PIN on screen is waiting for someone, and "Opening…" means an app is starting; check again later.
  if (!now && (!$("#pin-overlay").hidden || !$("#opening").hidden)) {
    idleTimer = window.setTimeout(() => goAmbient(false), IDLE_MS);
    return;
  }
  clearTimeout(idleTimer);
  disarm();
  if (view === "settings") showView("home");
  headerFocus = null;
  renderFocus();
  setAmbient(true);
}

/**
 * Ambient moves the widgets from above the tiles to the bottom of the screen (styles.css). They glide there rather
 * than jump: measure, switch, then animate from the old spot to the new one.
 */
function setAmbient(on: boolean): void {
  const widgets = $("#widgets");
  const before = widgets.hidden ? null : widgets.getBoundingClientRect();
  document.body.classList.toggle("ambient", on);
  if (!before) return;
  const after = widgets.getBoundingClientRect();
  const dx = before.left - after.left;
  const dy = before.top - after.top;
  if (Math.abs(dx) < 1 && Math.abs(dy) < 1) return;
  widgets.animate([{ transform: `translate(${dx}px, ${dy}px)` }, { transform: "translate(0, 0)" }], {
    duration: on ? 1600 : 400,
    easing: "cubic-bezier(0.2, 0, 0, 1)",
  });
}

// A click while ambient only wakes, like the first key press. Capture phase, so it never reaches a tile.
document.addEventListener(
  "click",
  (event) => {
    if (wake()) {
      event.stopPropagation();
      event.preventDefault();
    }
  },
  true,
);

// ---- Background photos settings ----

/** Choices for "Each photo stays up", in seconds. The default (30s) is in the middle. */
const SLIDE_STEPS = [5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600];
let photoMode: PhotoMode = "all";
/** When the slider was last moved here, so a listing arriving mid-drag doesn't yank it back. */
let slideTouched = 0;

function modeLabel(mode: PhotoMode): string {
  return mode === "all" ? "All photos" : (PHOTO_MODES.find((m) => m.mode === mode)?.label ?? mode);
}

function slideLabel(seconds: number): string {
  return seconds < 60 ? `${seconds}s` : `${seconds / 60} min`;
}

async function savePhotoSettings(patch: { mode?: PhotoMode; slideSeconds?: number }): Promise<void> {
  const response = await fetch("/api/photos/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(patch),
  });
  if (!response.ok) toast("Couldn't save the photo settings");
}

button("photo-mode", (el) => {
  const at = PHOTO_MODES.findIndex((m) => m.mode === photoMode);
  photoMode = PHOTO_MODES[(at + 1) % PHOTO_MODES.length].mode;
  el.textContent = `Show: ${modeLabel(photoMode)}`;
  void savePhotoSettings({ mode: photoMode });
});

{
  const slider = $<HTMLInputElement>("#slide-time");
  let saveTimer: number | undefined;
  slider.value = String(SLIDE_STEPS.indexOf(30));
  $("#slide-time-value").textContent = slideLabel(30);
  slider.addEventListener("input", () => {
    slideTouched = Date.now();
    const seconds = SLIDE_STEPS[Number(slider.value)];
    $("#slide-time-value").textContent = slideLabel(seconds);
    clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => void savePhotoSettings({ slideSeconds: seconds }), 300);
  });
}

function showPhotosInfo(list: PhotoList): void {
  photoMode = list.mode;
  $("#photo-mode").textContent = `Show: ${modeLabel(list.mode)}`;
  if (Date.now() - slideTouched > 2000) {
    // Nearest step, in case it was set to something else (by hand in the settings file, say).
    let best = 0;
    SLIDE_STEPS.forEach((s, i) => {
      if (Math.abs(s - list.slideSeconds) < Math.abs(SLIDE_STEPS[best] - list.slideSeconds)) best = i;
    });
    $<HTMLInputElement>("#slide-time").value = String(best);
    $("#slide-time-value").textContent = slideLabel(list.slideSeconds);
  }

  const videos = list.photos.filter((p) => p.kind === "video").length;
  const photos = list.photos.length - videos;
  const counts = [
    photos > 0 && `${photos} photo${photos === 1 ? "" : "s"}`,
    videos > 0 && `${videos} video${videos === 1 ? "" : "s"} (muted)`,
  ].filter(Boolean).join(" and ");
  const shown = list.photos.filter((p) => p.shown).length;
  const lines = [
    counts ? `${counts} in ${list.folder}.` : `None yet. They're kept in ${list.folder}.`,
    list.mode !== "all" && list.photos.length > 0 && `${shown} match ${modeLabel(list.mode)}.`,
    list.sorting.pending > 0 && `Sorting photos: ${list.sorting.pending} to go.`,
    list.sorting.status,
  ];
  $("#photos-info").textContent = lines.filter(Boolean).join(" ");
}

// ---- PIN ----

function showPin(pin: string, expiresAt: Date): void {
  $("#pin").textContent = pin;
  // The remote reads ?pin= and pairs straight away, so one scan does the whole thing.
  $("#pin-qr").replaceChildren(...(remoteUrl ? [qrSvg(`${remoteUrl}/remote/?pin=${encodeURIComponent(pin)}`)] : []));
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
startWidgets($("#widgets"));
const slideshow = startSlideshow($("#backdrop"), (list) => showPhotosInfo(list));
wake();

fetch("/api/apps")
  .then((r) => r.json() as Promise<AppTile[]>)
  .then((loaded) => {
    apps = loaded;
    renderTiles();
    renderSignIn();
    // After the apps, so cards can use their names and colors.
    void loadContinue();
  });
