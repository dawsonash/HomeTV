import type { PhotoList } from "../shared/protocol";

/** How long each photo stays up. Keep in step with the drift animation in styles.css. */
const SLIDE_MS = 30_000;
/** How often the photo folder is re-read, so new photos show up without a restart. */
const RELOAD_MS = 2 * 60_000;

/**
 * Crossfades through the photos in the box's photo folder, shuffled, reshuffling each time round. `container` holds
 * two <img> layers; the incoming one is decoded before it fades in, so a big photo never pops in half drawn.
 * Adds `has-photos` to <body> while a photo is showing. `onList` gets every folder listing (for Settings).
 */
export function startSlideshow(container: HTMLElement, onList: (list: PhotoList) => void): void {
  const layers = [...container.querySelectorAll<HTMLImageElement>("img")];
  let listing = "";
  let urls: string[] = [];
  /** Files the WebView couldn't draw (corrupt, HEIC...). Skipped until the folder changes. */
  let broken = new Set<string>();
  let queue: string[] = [];
  let current: string | null = null;
  let front = 0;
  let timer: number | undefined;
  let busy = false;

  const next = async () => {
    clearTimeout(timer);
    if (busy) return;
    busy = true;
    try {
      const playable = urls.filter((u) => !broken.has(u));
      while (playable.length > 0) {
        queue = queue.filter((u) => playable.includes(u));
        if (queue.length === 0) queue = shuffle(playable.filter((u) => u !== current || playable.length === 1));
        const url = queue.shift()!;
        if (url === current) break; // Only one photo: leave it up.
        const incoming = layers[1 - front];
        incoming.src = url;
        try {
          await incoming.decode();
        } catch {
          broken.add(url);
          playable.splice(playable.indexOf(url), 1);
          continue;
        }
        // The outgoing photo keeps drifting while it fades; the incoming one restarts its drift from the beginning.
        layers[front].classList.remove("shown");
        incoming.classList.remove("drift");
        void incoming.offsetWidth;
        incoming.classList.add("shown", "drift");
        front = 1 - front;
        current = url;
        break;
      }
      if (playable.length === 0) {
        current = null;
        layers.forEach((img) => img.classList.remove("shown"));
      }
      document.body.classList.toggle("has-photos", current !== null);
    } finally {
      busy = false;
    }
    timer = window.setTimeout(next, SLIDE_MS);
  };

  const reload = async () => {
    try {
      const response = await fetch("/api/photos");
      if (response.ok) {
        const list = (await response.json()) as PhotoList;
        onList(list);
        const fresh = list.photos.map((p) => p.url);
        if (fresh.join("\n") !== listing) {
          listing = fresh.join("\n");
          urls = fresh;
          broken = new Set();
          // Nothing up yet, or the photo on screen was deleted or edited: move on now rather than at the next slide.
          if (current === null || !urls.includes(current)) void next();
        }
      }
    } catch {
      // Server restarting; try again next time.
    }
    window.setTimeout(reload, RELOAD_MS);
  };
  void reload();
}

function shuffle<T>(items: T[]): T[] {
  for (let i = items.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [items[i], items[j]] = [items[j], items[i]];
  }
  return items;
}
