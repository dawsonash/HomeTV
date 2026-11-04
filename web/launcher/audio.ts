// The audio button in the launcher's top-left corner: one icon per output (soundbar, Echo Studio, TV speakers),
// the one playing lit up. Each press moves to the next one the box can reach.
import type { AudioOutput, AudioState } from "../shared/protocol";

const ICONS: Record<AudioOutput["icon"], string> = {
  tv: '<rect x="2" y="4" width="20" height="13" rx="2"/><path d="M8 21h8M12 17v4"/>',
  soundbar: '<rect x="1.5" y="9" width="21" height="6" rx="2"/><path d="M6 12h.01M10 12h.01M14 12h.01M18 12h.01"/>',
  speaker: '<rect x="6" y="2" width="12" height="20" rx="5"/><circle cx="12" cy="14" r="3"/><path d="M12 6.5h.01"/>',
};

let state: AudioState | null = null;
let busy = false;

/** Draws the button for the latest state from the box; hides it when audio.json lists no outputs. */
export function renderAudio(button: HTMLElement, next: AudioState): void {
  state = next;
  button.hidden = next.outputs.length === 0;

  const icons = next.outputs.map((output) => {
    const icon = document.createElement("span");
    icon.className = "audio-icon";
    icon.classList.toggle("current", output.id === next.current);
    icon.classList.toggle("unavailable", !output.available);
    icon.title = output.available ? output.name : `${output.name} (not available)`;
    icon.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true">${ICONS[output.icon] ?? ICONS.speaker}</svg>`;
    return icon;
  });
  const label = document.createElement("span");
  label.className = "audio-label";
  label.textContent = currentName() ?? "Sound";

  button.replaceChildren(...icons, label);
  button.setAttribute("aria-label", `Sound: ${currentName() ?? "other device"}. Press to switch.`);
}

function currentName(): string | null {
  return state?.outputs.find((o) => o.id === state?.current)?.name ?? null;
}

/** One press: switch to the next output. Calls `onError` with the reason if it didn't switch. */
export async function cycleAudio(button: HTMLElement, onError: (message: string) => void): Promise<void> {
  // A TV switch takes a second or so; presses meanwhile would only queue up surprises.
  if (busy || !state) return;
  busy = true;
  button.classList.add("busy");
  try {
    const response = await fetch("/api/audio/next", { method: "POST" });
    if (response.ok) {
      renderAudio(button, (await response.json()) as AudioState);
    } else {
      const body = (await response.json().catch(() => null)) as { error?: string } | null;
      onError(body?.error ?? "Couldn't switch the sound");
    }
  } catch {
    onError("Couldn't switch the sound");
  } finally {
    busy = false;
    button.classList.remove("busy");
  }
}
