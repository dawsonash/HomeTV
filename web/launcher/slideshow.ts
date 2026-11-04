import type { Photo, PhotoList } from "../shared/protocol";

/** How long each photo stays up unless Settings says otherwise (PhotoSettings.DefaultSlideSeconds). */
const DEFAULT_SLIDE_MS = 30_000;
/** A video plays through once (looping a short one up to the slide time), but never longer than this. */
const VIDEO_MAX_MS = 5 * 60_000;
/** A video that hasn't got far enough to play by then is skipped (an HEVC .mov without the codec, say). */
const VIDEO_LOAD_MS = 20_000;
/** Matches the opacity transition on .photo in styles.css. */
const FADE_MS = 2_500;
/** How often the photo folder is re-read, so new photos show up without a restart. */
const RELOAD_MS = 2 * 60_000;

export interface Slideshow {
  /** Pauses a playing video while an app covers the launcher, and the slide timer with it. */
  setPaused(paused: boolean): void;
  /** Re-reads the folder now (after an upload, say) instead of at the next poll. */
  reload(): void;
}

/**
 * Crossfades through the photos and videos the current mode shows (all, people, nature, safe for work), shuffled,
 * reshuffling each time round. Each slide is a new element in `container`, decoded (or buffered) before it fades in, so
 * nothing pops in half drawn; videos are always muted. Photos get a Ken Burns pan and zoom that always starts or ends
 * on the whole photo, over a blurred copy of itself where it doesn't fill the screen. Adds `has-photos` to <body>
 * while something is showing. `onList` gets every folder listing (for Settings).
 */
export function startSlideshow(container: HTMLElement, onList: (list: PhotoList) => void): Slideshow {
  let listing = "";
  let items: Photo[] = [];
  /** Files the WebView couldn't show (corrupt, HEIC, a video codec it lacks). Skipped until the folder changes. */
  let broken = new Set<string>();
  let queue: Photo[] = [];
  let current: Photo | null = null;
  let shown: HTMLElement | null = null;
  /** The pan and zoom on the photo that's up, paused along with the slideshow. */
  let motion: Animation | null = null;
  let slideMs = DEFAULT_SLIDE_MS;
  let timer: number | undefined;
  let reloadTimer: number | undefined;
  let busy = false;
  let paused = false;
  /** When the current slide's timer would fire, and how long it had left when paused. */
  let dueAt = 0;
  let remaining = 0;

  const schedule = (ms: number) => {
    clearTimeout(timer);
    dueAt = Date.now() + ms;
    remaining = ms;
    if (!paused) timer = window.setTimeout(next, ms);
  };

  const load = async (item: Photo): Promise<HTMLImageElement | HTMLVideoElement | null> => {
    if (item.kind === "image") {
      const img = document.createElement("img");
      img.alt = "";
      img.src = item.url;
      try {
        await img.decode();
        return img;
      } catch {
        return null;
      }
    }
    const video = document.createElement("video");
    // Muted three ways: the property, the default, and the attribute (which is what lets it autoplay).
    video.muted = true;
    video.defaultMuted = true;
    video.setAttribute("muted", "");
    video.playsInline = true;
    video.loop = true;
    video.preload = "auto";
    video.src = item.url;
    const ready = await new Promise<boolean>((resolve) => {
      const done = (ok: boolean) => {
        clearTimeout(giveUp);
        video.removeEventListener("canplay", onReady);
        video.removeEventListener("error", onError);
        resolve(ok);
      };
      const onReady = () => done(true);
      const onError = () => done(false);
      const giveUp = window.setTimeout(() => done(false), VIDEO_LOAD_MS);
      video.addEventListener("canplay", onReady);
      video.addEventListener("error", onError);
    });
    if (!ready || video.videoWidth === 0) {
      video.removeAttribute("src");
      video.load();
      return null;
    }
    return video;
  };

  const next = async () => {
    clearTimeout(timer);
    if (busy) return;
    busy = true;
    let duration = slideMs;
    try {
      const playable = () => items.filter((p) => !broken.has(p.url));
      while (playable().length > 0) {
        const urls = new Set(playable().map((p) => p.url));
        queue = queue.filter((p) => urls.has(p.url));
        if (queue.length === 0) queue = shuffle(playable().filter((p) => p.url !== current?.url || urls.size === 1));
        const item = queue.shift()!;
        if (item.url === current?.url) {
          // Only one: leave it up (a video keeps looping, a photo rests on the whole picture).
          if (shown?.querySelector("video")) duration = VIDEO_MAX_MS;
          break;
        }

        const media = await load(item);
        if (!media) {
          broken.add(item.url);
          continue;
        }
        const slide = document.createElement("div");
        slide.className = "photo";
        let nextMotion: Animation | null = null;
        if (media instanceof HTMLVideoElement) {
          media.className = "photo-video";
          slide.append(media);
          try {
            if (!paused) await media.play();
          } catch {
            broken.add(item.url);
            continue;
          }
          const seconds = Number.isFinite(media.duration) ? media.duration : 0;
          duration = Math.min(VIDEO_MAX_MS, Math.max(slideMs, seconds * 1000));
          container.append(slide);
        } else {
          // Behind the photo, a blurred copy fills whatever the whole photo leaves uncovered.
          const fill = document.createElement("img");
          fill.className = "photo-fill";
          fill.alt = "";
          fill.src = item.url;
          media.className = "photo-image";
          slide.append(fill, media);
          container.append(slide);
          nextMotion = kenBurns(media, container, slideMs + 2 * FADE_MS);
          if (paused) nextMotion?.pause();
        }

        // The outgoing slide keeps going while it fades, then goes away; the incoming one fades in over it.
        const outgoing = shown;
        void slide.offsetWidth;
        slide.classList.add("shown");
        outgoing?.classList.remove("shown");
        if (outgoing) window.setTimeout(() => dispose(outgoing), FADE_MS + 100);
        shown = slide;
        motion = nextMotion;
        current = item;
        break;
      }
      if (playable().length === 0 && shown) {
        const outgoing = shown;
        outgoing.classList.remove("shown");
        window.setTimeout(() => dispose(outgoing), FADE_MS + 100);
        shown = null;
        motion = null;
        current = null;
      }
      document.body.classList.toggle("has-photos", current !== null);
    } finally {
      busy = false;
    }
    schedule(duration);
  };

  const reload = async () => {
    clearTimeout(reloadTimer);
    try {
      const response = await fetch("/api/photos");
      if (response.ok) {
        const list = (await response.json()) as PhotoList;
        onList(list);
        const nextSlideMs = Math.max(1, list.slideSeconds || 30) * 1000;
        if (nextSlideMs !== slideMs) {
          slideMs = nextSlideMs;
          // Shorter now: don't sit out the rest of the old, longer time.
          if (current && !paused && !shown?.querySelector("video") && dueAt - Date.now() > slideMs) schedule(slideMs);
        }
        const playlist = list.photos.filter((p) => p.shown);
        const fresh = playlist.map((p) => p.url).join("\n");
        if (fresh !== listing) {
          listing = fresh;
          items = playlist;
          broken = new Set();
          // Nothing up yet, or the one on screen was deleted or edited: move on now rather than at the next slide.
          if (current === null || !items.some((p) => p.url === current!.url)) void next();
        }
      }
    } catch {
      // Server restarting; try again next time.
    }
    reloadTimer = window.setTimeout(reload, RELOAD_MS);
  };
  void reload();

  return {
    setPaused(value: boolean) {
      if (value === paused) return;
      paused = value;
      const video = shown?.querySelector("video") ?? null;
      if (paused) {
        clearTimeout(timer);
        remaining = Math.max(0, dueAt - Date.now());
        video?.pause();
        motion?.pause();
      } else {
        void video?.play().catch(() => {});
        motion?.play();
        schedule(remaining || slideMs);
      }
    },
    reload: () => void reload(),
  };
}

/** Removes a slide, stopping its video downloading and decoding. */
function dispose(slide: HTMLElement): void {
  for (const video of slide.querySelectorAll("video")) {
    video.pause();
    video.removeAttribute("src");
    video.load();
  }
  slide.remove();
}

/**
 * Ken Burns: the photo is sized to fit whole on screen, then slowly zooms and pans between that and a close-up that
 * fills the screen (a portrait photo's close-up crops to the middle, a landscape one's a little tighter). Half the
 * time it starts close and pulls back, half the time it starts whole and pushes in; either way it holds on the whole
 * photo for a moment, so every part of it is seen.
 */
function kenBurns(img: HTMLImageElement, container: HTMLElement, durationMs: number): Animation | null {
  const width = container.clientWidth;
  const height = container.clientHeight;
  const natural = { w: img.naturalWidth, h: img.naturalHeight };
  if (!width || !height || !natural.w || !natural.h) return null;

  // Sized and centred to fit (CSS "contain"); the animation only scales and moves it from there.
  const fit = Math.min(width / natural.w, height / natural.h);
  const w = natural.w * fit;
  const h = natural.h * fit;
  Object.assign(img.style, { width: `${w}px`, height: `${h}px`, left: `${(width - w) / 2}px`, top: `${(height - h) / 2}px` });

  if (matchMedia("(prefers-reduced-motion: reduce)").matches) return null;

  // Close-up: enough to cover the screen, plus a bit, so even a 16:9 photo moves. Capped for panoramas.
  const cover = Math.max(width / w, height / h);
  const zoom = Math.min(2.5, Math.max(1.15, cover * 1.05));
  // Somewhere within the photo, leaning towards the top third, where faces usually are.
  const slackX = Math.max(0, (w * zoom - width) / 2);
  const slackY = Math.max(0, (h * zoom - height) / 2);
  const x = slackX * (Math.random() * 2 - 1) * 0.7;
  const y = slackY * (0.15 + Math.random() * 0.6);
  const close = { transform: `translate(${x}px, ${y}px) scale(${zoom})` };
  const whole = { transform: "translate(0px, 0px) scale(1)" };

  const pullBack = Math.random() < 0.5;
  const keyframes = pullBack
    ? [{ ...close, offset: 0 }, { ...whole, offset: 0.8 }, { ...whole, offset: 1 }]
    : [{ ...whole, offset: 0 }, { ...whole, offset: 0.15 }, { ...close, offset: 1 }];
  return img.animate(keyframes, { duration: durationMs, easing: "ease-in-out", fill: "forwards" });
}

function shuffle<T>(items: T[]): T[] {
  for (let i = items.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [items[i], items[j]] = [items[j], items[i]];
  }
  return items;
}
