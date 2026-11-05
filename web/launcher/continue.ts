import type { AppTile, ContinueEntry } from "../shared/protocol";

/** Cards that fit across the TV at their size; the server keeps a few more. */
export const MAX_CARDS = 6;

export async function fetchContinue(): Promise<ContinueEntry[]> {
  try {
    const response = await fetch("/api/continue");
    if (!response.ok) return [];
    return ((await response.json()) as ContinueEntry[]).slice(0, MAX_CARDS);
  } catch {
    return [];
  }
}

/** Fraction watched, or null when there's no known length (live TV) to measure against. */
export function progress(entry: ContinueEntry): number | null {
  if (entry.position == null || !entry.duration) return null;
  return Math.max(0, Math.min(1, entry.position / entry.duration));
}

/** Takes a card out of the row for good (until that video is played again). */
export async function removeContinue(id: string): Promise<boolean> {
  try {
    const response = await fetch(`/api/continue/${encodeURIComponent(id)}`, { method: "DELETE" });
    return response.ok || response.status === 404;
  } catch {
    return false;
  }
}

/**
 * Draws the row. `onPoint`, `onOpen` and `onRemove` get the card's index, so the mouse selects, opens and removes
 * cards the same way the D-pad does.
 */
export function renderContinue(
  section: HTMLElement,
  row: HTMLElement,
  entries: ContinueEntry[],
  apps: AppTile[],
  onPoint: (index: number) => void,
  onOpen: (index: number) => void,
  onRemove: (index: number) => void,
): void {
  section.hidden = entries.length === 0;
  row.replaceChildren(
    ...entries.map((entry, i) => {
      const app = apps.find((a) => a.id === entry.appId);

      const card = document.createElement("div");
      card.className = "card";
      card.role = "listitem";

      const art = document.createElement("div");
      art.className = "card-art";
      if (app?.color) art.style.setProperty("--tile-color", app.color);
      const label = document.createElement("span");
      label.className = "app-name";
      label.textContent = app?.name ?? "";
      if (entry.image) {
        const img = document.createElement("img");
        img.alt = "";
        img.referrerPolicy = "no-referrer";
        img.src = entry.image;
        // A dead image falls back to the app's color and name.
        img.addEventListener("error", () => img.replaceWith(label));
        art.append(img);
      } else {
        art.append(label);
      }

      const fraction = progress(entry);
      if (fraction !== null) {
        const bar = document.createElement("div");
        bar.className = "card-progress";
        const fill = document.createElement("span");
        fill.style.width = `${(fraction * 100).toFixed(1)}%`;
        bar.append(fill);
        art.append(bar);
      }

      // ✕ in the corner: shown on the selected (or hovered) card. Up from a card selects it.
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "card-remove";
      remove.tabIndex = -1;
      remove.setAttribute("aria-label", `Remove ${entry.title} from Continue watching`);
      remove.textContent = "✕";
      remove.addEventListener("click", (event) => {
        event.stopPropagation();
        onRemove(i);
      });
      art.append(remove);

      const title = document.createElement("div");
      title.className = "card-title";
      title.textContent = entry.title;

      card.append(art, title);
      card.addEventListener("mouseenter", () => onPoint(i));
      card.addEventListener("click", () => onOpen(i));
      return card;
    }),
  );
}
