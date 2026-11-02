import type { AppTile, RemoteKey } from "../shared/protocol";
import { HubSocket } from "../shared/socket";

const $ = <T extends HTMLElement>(selector: string) => document.querySelector<T>(selector)!;

const REPEAT_DELAY_MS = 400;
const REPEAT_INTERVAL_MS = 110;
const POINTER_SPEED = 2.2;
const TAP_MAX_MS = 250;
const TAP_MAX_TRAVEL = 8;

let socket: HubSocket | null = null;
let apps: AppTile[] = [];

const buzz = () => navigator.vibrate?.(8);

async function start(): Promise<void> {
  const status = await fetch("/api/pair/status").then((r) => r.json() as Promise<{ paired: boolean }>);
  if (status.paired) showMain();
  else showPairing();
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

$<HTMLFormElement>("#pair-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const response = await fetch("/api/pair", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ pin: $<HTMLInputElement>("#pin").value, deviceName: $<HTMLInputElement>("#device-name").value }),
  });
  $<HTMLInputElement>("#pin").value = "";
  if (response.ok) {
    setPairMessage("");
    showMain();
  } else if (response.status === 401) {
    setPairMessage("Wrong PIN, try again.", true);
  } else {
    $("#pair-form").hidden = true;
    setPairMessage("That PIN expired. Ask for a new one.", true);
  }
});

// ---------- Main remote ----------

async function showMain(): Promise<void> {
  $("#pair").hidden = true;
  $("#main").hidden = false;

  socket = new HubSocket({
    path: "/ws/remote",
    onStatus: (online) => $("#status-dot").classList.toggle("online", online),
    onEvent: (event) => {
      if (event.type === "state") {
        $("#now-showing").textContent = apps.find((a) => a.id === event.foregroundApp)?.name ?? "Home";
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

  const response = await fetch("/api/apps");
  if (response.ok) {
    apps = await response.json();
    renderApps();
  }
}

function renderApps(): void {
  const container = $("#apps");
  container.replaceChildren(
    ...apps.map((app) => {
      const button = document.createElement("button");
      button.textContent = app.name;
      if (app.color) button.style.background = app.color;
      button.addEventListener("click", () => {
        buzz();
        socket?.send({ type: "launch", app: app.id });
        selectTab("remote");
      });
      return button;
    }),
  );
}

function selectTab(name: string): void {
  document.querySelectorAll<HTMLElement>(".tab").forEach((tab) => (tab.hidden = tab.id !== `tab-${name}`));
  document.querySelectorAll<HTMLElement>("nav button").forEach((b) => b.classList.toggle("active", b.dataset.tab === name));
}

document.querySelectorAll<HTMLElement>("nav button").forEach((button) =>
  button.addEventListener("click", () => selectTab(button.dataset.tab!)),
);

// Key buttons; those marked data-repeat auto-repeat while held, like a real remote.
document.querySelectorAll<HTMLElement>("[data-key]").forEach((button) => {
  const key = button.dataset.key as RemoteKey;
  const press = () => socket?.send({ type: "key", key });
  let delay: number | undefined;
  let repeat: number | undefined;

  const stop = () => {
    clearTimeout(delay);
    clearInterval(repeat);
    button.classList.remove("pressed");
  };

  button.addEventListener("pointerdown", (event) => {
    event.preventDefault();
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
});

$("#home").addEventListener("click", () => {
  buzz();
  socket?.send({ type: "home" });
});

$("#sleep").addEventListener("click", () => {
  if (confirm("Put the TV box to sleep?")) socket?.send({ type: "sleep" });
});

$("#desktop").addEventListener("click", () => {
  if (confirm("Open the Windows desktop on the TV?")) socket?.send({ type: "desktop" });
});

// ---------- Touchpad ----------

function trackDrag(element: HTMLElement, onDelta: (dx: number, dy: number) => void, onTap?: () => void): void {
  let last: { x: number; y: number } | null = null;
  let travel = 0;
  let downAt = 0;
  let pending = { dx: 0, dy: 0 };
  let frame = 0;

  // Coalesce pointer events into one message per animation frame.
  const flush = () => {
    frame = 0;
    const dx = Math.round(pending.dx);
    const dy = Math.round(pending.dy);
    if (dx || dy) onDelta(dx, dy);
    pending = { dx: pending.dx - dx, dy: pending.dy - dy };
  };

  element.addEventListener("pointerdown", (event) => {
    element.setPointerCapture(event.pointerId);
    last = { x: event.clientX, y: event.clientY };
    travel = 0;
    downAt = performance.now();
  });
  element.addEventListener("pointermove", (event) => {
    if (!last) return;
    const dx = event.clientX - last.x;
    const dy = event.clientY - last.y;
    last = { x: event.clientX, y: event.clientY };
    travel += Math.abs(dx) + Math.abs(dy);
    pending.dx += dx;
    pending.dy += dy;
    frame ||= requestAnimationFrame(flush);
  });
  element.addEventListener("pointerup", () => {
    if (onTap && travel < TAP_MAX_TRAVEL && performance.now() - downAt < TAP_MAX_MS) {
      buzz();
      onTap();
    }
    last = null;
  });
  element.addEventListener("pointercancel", () => (last = null));
}

trackDrag(
  $("#touchpad"),
  (dx, dy) => socket?.send({ type: "mouseMove", dx: Math.round(dx * POINTER_SPEED), dy: Math.round(dy * POINTER_SPEED) }),
  () => socket?.send({ type: "click", button: "left" }),
);
// Dragging up on the strip scrolls the page down, like a phone.
trackDrag($("#scrollstrip"), (_dx, dy) => socket?.send({ type: "scroll", dy: -dy }));

$("#left-click").addEventListener("click", () => socket?.send({ type: "click", button: "left" }));
$("#right-click").addEventListener("click", () => socket?.send({ type: "click", button: "right" }));

// ---------- Keyboard ----------

$<HTMLFormElement>("#text-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const input = $<HTMLInputElement>("#text");
  if (input.value) socket?.send({ type: "text", text: input.value });
  input.value = "";
});

start();
