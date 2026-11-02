import type { HubEvent, RemoteCommand } from "./protocol";

export interface HubSocketOptions {
  path: string;
  onEvent: (event: HubEvent) => void;
  onStatus?: (connected: boolean) => void;
  /** Called after a drop, before reconnecting. Return false to stop (e.g. the remote got unpaired). */
  shouldReconnect?: () => Promise<boolean>;
}

/** A WebSocket to the HomeTV server that reconnects with backoff (the box sleeps, WiFi drops). */
export class HubSocket {
  private socket: WebSocket | null = null;
  private retryMs = 500;
  private stopped = false;

  constructor(private readonly options: HubSocketOptions) {
    this.connect();
    // Phones suspend background tabs; reconnect immediately when the remote comes back on screen.
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible" && !this.connected) this.reconnectNow();
    });
  }

  get connected(): boolean {
    return this.socket?.readyState === WebSocket.OPEN;
  }

  send(command: RemoteCommand): void {
    if (this.connected) this.socket!.send(JSON.stringify(command));
  }

  stop(): void {
    this.stopped = true;
    this.socket?.close();
  }

  private connect(): void {
    const scheme = location.protocol === "https:" ? "wss" : "ws";
    const socket = new WebSocket(`${scheme}://${location.host}${this.options.path}`);
    this.socket = socket;

    socket.onopen = () => {
      this.retryMs = 500;
      this.options.onStatus?.(true);
    };
    socket.onmessage = (message) => this.options.onEvent(JSON.parse(message.data) as HubEvent);
    socket.onclose = async () => {
      if (this.socket !== socket) return;
      this.options.onStatus?.(false);
      if (this.stopped || !(await (this.options.shouldReconnect?.() ?? Promise.resolve(true)))) return;
      setTimeout(() => this.socket === socket && this.connect(), this.retryMs);
      this.retryMs = Math.min(this.retryMs * 2, 5000);
    };
  }

  private reconnectNow(): void {
    this.retryMs = 500;
    const old = this.socket;
    this.socket = null;
    old?.close();
    this.connect();
  }
}
