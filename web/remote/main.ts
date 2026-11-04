import type { AppTile, RemoteKey, TextField } from "../shared/protocol";
import { HubSocket } from "../shared/socket";

const $ = <T extends HTMLElement>(selector: string) => document.querySelector<T>(selector)!;

const REPEAT_DELAY_MS = 400;
const REPEAT_INTERVAL_MS = 110;
const POINTER_SPEED = 3.74; // 2.2 × 1.7
const TAP_MAX_MS = 250;
const TAP_MAX_TRAVEL = 8;

let socket: HubSocket | null = null;
let apps: AppTile[] = [];
let foregroundApp: string | null = null;
/** Open apps, most recently used first. */
let running: string[] = [];
/** Tiled apps in screen order, while in split screen. */
let split: string[] | null = null;

const buzz = () => navigator.vibrate?.(8);

async function start(): Promise<void> {
  // The QR on the TV's PIN screen carries the PIN. Drop it from the address bar so it stays out of history.
  const params = new URLSearchParams(location.search);
  const pin = params.get("pin");
  if (pin !== null) history.replaceState(null, "", location.pathname);

  const status = await fetch("/api/pair/status").then((r) => r.json() as Promise<{ paired: boolean }>);
  if (status.paired) {
    showMain();
  } else {
    showPairing();
    if (pin && /^[0-9]{6}$/.test(pin)) {
      $("#pair-form").hidden = false;
      setPairMessage("Pairing…");
      void pair(pin);
    }
  }
}

// ---------- Pairing ----------

function showPairing(): void {
  socket?.stop();
  socket = null;
  $("#main").hidden = true;
  $("#pair").hidden = false;
  $<HTMLInputElement>("#device-name").value ||= guessDeviceName();
}

function guessDeviceName(): string {
  const ua = navigator.userAgent;
  if (/iPhone/.test(ua)) return "iPhone";
  if (/iPad/.test(ua)) return "iPad";
  if (/Android/.test(ua)) return "Android phone";
  return "Phone";
}

function setPairMessage(text: string, error = false): void {
  const message = $("#pair-message");
  message.textContent = text;
  message.classList.toggle("error", error);
}

$("#show-pin").addEventListener("click", async () => {
  const response = await fetch("/api/pair/request", { method: "POST" });
  if (response.status === 429) {
    const { retryAfterSeconds } = await response.json();
    setPairMessage(`Wait ${retryAfterSeconds}s before asking again.`, true);
    return;
  }
  $("#pair-form").hidden = false;
  setPairMessage("Enter the PIN shown on your TV.");
  $<HTMLInputElement>("#pin").focus();
});

$<HTMLFormElement>("#pair-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const input = $<HTMLInputElement>("#pin");
  const pin = input.value;
  input.value = "";
  void pair(pin);
});

async function pair(pin: string): Promise<void> {
  const response = await fetch("/api/pair", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ pin, deviceName: $<HTMLInputElement>("#device-name").value }),
  });
  if (response.ok) {
    setPairMessage("");
    showMain();
  } else if (response.status === 401) {
    setPairMessage("Wrong PIN, try again.", true);
  } else {
    $("#pair-form").hidden = true;
    setPairMessage("That PIN expired. Ask for a new one.", true);
  }
}

// ---------- Main remote ----------

async function showMain(): Promise<void> {
  $("#pair").hidden = true;
  $("#main").hidden = false;

  socket = new HubSocket({
    path: "/ws/remote",
    onStatus: (online) => $("#status-dot").classList.toggle("online", online),
    onEvent: (event) => {
      if (event.type === "state") {
        foregroundApp = event.foregroundApp;
        running = event.running ?? [];
        split = event.split ?? null;
        renderState();
      } else if (event.type === "textFocus") {
        onTextFocus(event.field);
      } else if (event.type === "scrollable") {
        // The strip only shows when there's something to scroll; the touchpad takes its space otherwise.
        $("#scrollstrip").hidden = !event.scrollable;
      }
    },
    // A failed upgrade looks like a plain close; check whether this phone was unpaired.
    shouldReconnect: async () => {
      try {
        const { paired } = await fetch("/api/pair/status").then((r) => r.json());
        if (!paired) showPairing();
        return paired;
      } catch {
        return true; // Box asleep or unreachable: keep retrying.
      }
    },
  });

  // Only for the names in "Open now".
  const response = await fetch("/api/apps");
  if (response.ok) {
    apps = await response.json();
    renderState(); // The state may have arrived before the app names did.
  }
}

const appName = (id: string) => apps.find((a) => a.id === id)?.name ?? id;

/** Header name, and "Open now" above the touchpad. */
function renderState(): void {
  $("#now-showing").textContent = split
    ? split.map(appName).join(" + ")
    : foregroundApp ? appName(foregroundApp) : "Home";

  $("#open-list").hidden = running.length === 0;
  $("#open-list").replaceChildren(
    ...running.map((id) => {
      const tiled = !!split?.includes(id);
      const item = document.createElement("li");
      item.classList.toggle("on-screen", id === foregroundApp);

      // Switches to the app; in split screen, a tile's app just gets the sound and the keys.
      const show = document.createElement("button");
      show.className = "open-show";
      const name = document.createElement("span");
      name.textContent = appName(id);
      const where = document.createElement("span");
      where.className = "open-where";
      where.textContent = tiled ? (id === foregroundApp ? "Split · sound" : "Split") : id === foregroundApp ? "On screen" : "";
      show.append(name, where);
      show.addEventListener("click", () => {
        buzz();
        socket?.send({ type: "launch", app: id });
      });

      const splitButton = document.createElement("button");
      splitButton.className = "open-split";
      splitButton.textContent = "⧉";
      splitButton.classList.toggle("active", tiled);
      // Nothing to split with (the only open app), or no room for another tile.
      splitButton.hidden = split ? !tiled && split.length >= MAX_TILES : !splitPartner(id);
      splitButton.setAttribute("aria-pressed", String(tiled));
      splitButton.setAttribute("aria-label", tiled ? `Take ${appName(id)} out of split screen` : `Split screen with ${appName(id)}`);
      splitButton.addEventListener("click", () => {
        buzz();
        toggleSplit(id);
      });

      const close = document.createElement("button");
      close.className = "open-close";
      close.setAttribute("aria-label", `Close ${appName(id)}`);
      close.textContent = "✕";
      close.addEventListener("click", () => {
        buzz();
        socket?.send({ type: "close", app: id });
      });

      item.append(show, splitButton, close);
      return item;
    }),
  );
}

const MAX_TILES = 4;

/** Another open app to put beside `id` in split screen: what's on screen, or else the most recently used. */
function splitPartner(id: string): string | undefined {
  return foregroundApp && foregroundApp !== id ? foregroundApp : running.find((r) => r !== id);
}

/**
 * The ⧉ next to an open app. In split screen it adds the app as a tile or takes it out (down to one, which goes
 * full screen). Otherwise it puts the app side by side with another open one.
 */
function toggleSplit(id: string): void {
  if (split) {
    if (split.includes(id)) socket?.send({ type: "split", apps: split.filter((s) => s !== id) });
    else if (split.length < MAX_TILES) socket?.send({ type: "split", apps: [...split, id] });
    return;
  }
  const partner = splitPartner(id);
  if (partner) socket?.send({ type: "split", apps: id === foregroundApp ? [id, partner] : [partner, id] });
}

// Key buttons; those marked data-repeat auto-repeat while held, like a real remote.
document.querySelectorAll<HTMLElement>("[data-key]").forEach((button) => {
  const key = button.dataset.key as RemoteKey;
  onPress(button, key === "backspace" ? deleteOne : () => socket?.send({ type: "key", key }));
});

/** Runs `press` on touch (not release), and again while held if the button is marked data-repeat. */
function onPress(button: HTMLElement, press: () => void): void {
  let delay: number | undefined;
  let repeat: number | undefined;

  const stop = () => {
    clearTimeout(delay);
    clearInterval(repeat);
    button.classList.remove("pressed");
  };

  button.addEventListener("pointerdown", (event) => {
    event.preventDefault(); // Also keeps focus (and the phone keyboard) in the text field.
    button.classList.add("pressed");
    buzz();
    press();
    if (button.hasAttribute("data-repeat")) {
      delay = window.setTimeout(() => (repeat = window.setInterval(press, REPEAT_INTERVAL_MS)), REPEAT_DELAY_MS);
    }
  });
  button.addEventListener("pointerup", stop);
  button.addEventListener("pointercancel", stop);
  button.addEventListener("pointerleave", stop);
}

$("#home").addEventListener("click", () => {
  buzz();
  socket?.send({ type: "home" });
});

$("#sleep").addEventListener("click", () => {
  if (confirm("Put the TV box to sleep?")) socket?.send({ type: "sleep" });
});

// ---------- Touchpad ----------

/**
 * One finger drags (onDelta) and taps (onTap). Two fingers tapped together call onTwoFingerTap and don't move
 * anything. Moves are coalesced into one message per animation frame.
 */
function trackDrag(
  element: HTMLElement,
  onDelta: (dx: number, dy: number) => void,
  onTap?: () => void,
  onTwoFingerTap?: () => void,
): void {
  const fingers = new Map<number, { x: number; y: number }>();
  let mostFingers = 0;
  let travel = 0;
  let downAt = 0;
  let pending = { dx: 0, dy: 0 };
  let frame = 0;

  const flush = () => {
    frame = 0;
    const dx = Math.round(pending.dx);
    const dy = Math.round(pending.dy);
    if (dx || dy) onDelta(dx, dy);
    pending = { dx: pending.dx - dx, dy: pending.dy - dy };
  };

  element.addEventListener("pointerdown", (event) => {
    element.setPointerCapture(event.pointerId);
    if (fingers.size === 0) {
      mostFingers = 0;
      travel = 0;
      downAt = performance.now();
    }
    fingers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    mostFingers = Math.max(mostFingers, fingers.size);
  });
  element.addEventListener("pointermove", (event) => {
    const last = fingers.get(event.pointerId);
    if (!last) return;
    const dx = event.clientX - last.x;
    const dy = event.clientY - last.y;
    fingers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    travel += Math.abs(dx) + Math.abs(dy);
    if (mostFingers > 1) return; // A second finger landed: this is a gesture, not a drag.
    pending.dx += dx;
    pending.dy += dy;
    frame ||= requestAnimationFrame(flush);
  });
  const lift = (event: PointerEvent, cancelled: boolean) => {
    if (!fingers.delete(event.pointerId) || fingers.size > 0) return;
    if (cancelled || travel >= TAP_MAX_TRAVEL * mostFingers || performance.now() - downAt >= TAP_MAX_MS * 1.5) return;
    const tap = mostFingers === 1 ? onTap : mostFingers === 2 ? onTwoFingerTap : undefined;
    if (!tap) return;
    buzz();
    // On a touch screen, run it from the touchend that follows: iOS counts that as the tap for opening its
    // keyboard (see tap()), and isn't as sure about pointerup.
    if (event.pointerType === "touch" && "ontouchend" in window) pendingTap = tap;
    else tap();
  };
  let pendingTap: (() => void) | undefined;
  element.addEventListener("pointerup", (event) => lift(event, false));
  element.addEventListener("pointercancel", (event) => lift(event, true));
  element.addEventListener("touchend", () => {
    const tap = pendingTap;
    pendingTap = undefined;
    tap?.();
  });
  element.addEventListener("touchcancel", () => (pendingTap = undefined));
}

trackDrag(
  $("#touchpad"),
  (dx, dy) => socket?.send({ type: "mouseMove", dx: Math.round(dx * POINTER_SPEED), dy: Math.round(dy * POINTER_SPEED) }),
  tap,
  () => socket?.send({ type: "click", button: "right" }),
);
// Dragging up on the strip scrolls the page down, like a phone.
trackDrag($("#scrollstrip"), (_dx, dy) => socket?.send({ type: "scroll", dy: -dy }));

// ---------- Keyboard ----------
//
// The phone's field mirrors the TV's: each edit is diffed against what was already sent, and the difference goes
// over as backspaces plus new text. That way autocorrect and the phone's own delete key carry over too.

const keyboard = $<HTMLFormElement>("#keyboard");
const textInput = $<HTMLInputElement>("#text");
/** Characters (code points) the TV has been sent since the phone's field was last cleared. */
let typed: string[] = [];
/** Under CommandDispatcher.MaxTextLength (counted in UTF-16 units) even if every character is a surrogate pair. */
const TEXT_CHUNK = 240;

/** The TV field the keyboard is open for, to tell a new field from the same one reported twice. */
let shownField: TextField | null | undefined;

function showKeyboard(field: TextField | null): void {
  const same = !keyboard.hidden && !!shownField && !!field && shownField.name === field.name && shownField.secret === field.secret;
  shownField = field;
  keyboard.hidden = false;
  textInput.type = field?.secret ? "password" : "text";
  textInput.placeholder = field?.name ? `Type in ${field.name}…` : "Type on TV…";
  if (!same) clearText();
  // The phone keyboard only comes up if this runs during a tap (see tap()); otherwise the field waits to be tapped.
  textInput.focus({ preventScroll: true });
}

/**
 * A touchpad tap. The click goes as a request the page waits on (a quarter of a second at most) for the box to say
 * whether it put focus in a text field. If it did, the phone's field is focused while this is still the tap's
 * handler, which is the only time iOS opens its keyboard for it.
 */
function tap(): void {
  let field: TextField | null = null;
  try {
    const request = new XMLHttpRequest();
    request.open("POST", "/api/tap", false);
    request.send();
    if (request.status !== 200) {
      // Not clicked (unauthorised, or an older box without /api/tap): click the usual way.
      socket?.send({ type: "click", button: "left" });
      return;
    }
    field = (JSON.parse(request.responseText) as { field: TextField | null }).field;
  } catch {
    return; // Box unreachable; the socket's reconnecting anyway.
  }
  if (field) showKeyboard(field);
}

function hideKeyboard(): void {
  shownField = undefined;
  keyboard.hidden = true;
  textInput.blur();
  clearText();
}

function clearText(): void {
  textInput.value = "";
  typed = [];
}

function onTextFocus(field: TextField | null): void {
  if (field) showKeyboard(field);
  else if (!keyboard.hidden) hideKeyboard();
}

textInput.addEventListener("input", () => {
  const next = Array.from(textInput.value);
  let same = 0;
  while (same < typed.length && same < next.length && typed[same] === next[same]) same++;
  for (let i = same; i < typed.length; i++) socket?.send({ type: "key", key: "backspace" });
  for (let i = same; i < next.length; i += TEXT_CHUNK) socket?.send({ type: "text", text: next.slice(i, i + TEXT_CHUNK).join("") });
  typed = next;
});

textInput.addEventListener("keydown", (event) => {
  // With the field empty there's no input event to diff, but the TV's field may still have text in it.
  if (event.key === "Backspace" && textInput.value === "") socket?.send({ type: "key", key: "backspace" });
});

// Enter, or the keyboard's Go key.
keyboard.addEventListener("submit", (event) => {
  event.preventDefault();
  buzz();
  socket?.send({ type: "key", key: "enter" });
  clearText();
});

/** The ⌫ button: deletes on the TV and keeps the phone's field in step. */
function deleteOne(): void {
  socket?.send({ type: "key", key: "backspace" });
  if (typed.length === 0) return;
  typed = typed.slice(0, -1);
  textInput.value = typed.join("");
}

$("#keyboard-close").addEventListener("click", hideKeyboard);

start();
