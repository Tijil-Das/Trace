import type { FrameEvent, HostMessage, HostMessageOf, HostMessageType, OutgoingCommand, RecallConfig } from './protocol';
import { isFrameEvent } from './protocol';
import { createTransport, type HostTransport, type TransportKind } from './transport';

/**
 * Typed facade over the host transport.
 *
 * Commands are one method each (matching the frozen protocol), and incoming messages are
 * delivered through `on(type, handler)`. Frames take the fast path: `onFrame` hands over the
 * pixel view straight from the WebView2 shared buffer, and subscribers must copy or draw
 * synchronously because the transport releases the buffer as soon as they return.
 */
export interface Bridge {
  /** `webview` in the WPF host, `mock` in the dev fallback. */
  readonly kind: TransportKind;
  dispose(): void;

  /* incoming ------------------------------------------------------------- */
  onFrame(handler: (frame: FrameEvent) => void): () => void;
  on<T extends HostMessageType>(type: T, handler: (message: HostMessageOf<T>) => void): () => void;

  /* day / replay navigation ---------------------------------------------- */
  requestDays(): void;
  requestReplays(day: string): void;
  openDay(day: string): void;

  /* transport ------------------------------------------------------------ */
  play(): void;
  pause(): void;
  togglePlay(): void;
  seek(positionUs: number): void;
  seekProgress(progress: number): void;
  setSpeed(value: number): void;
  step(direction: 1 | -1): void;
  skipIdle(): void;

  /* navigation metadata -------------------------------------------------- */
  requestIdles(minGapUs?: number): void;
  requestSpans(): void;

  /* capture service ------------------------------------------------------ */
  savePng(): void;
  requestStatus(): void;
  requestConfig(): void;
  saveConfig(config: RecallConfig): void;
  pauseCapture(): void;
  resumeCapture(): void;
  purgeRecent(minutes: number): void;
  prune(): void;
  openStorage(): void;
  refresh(): void;

  /* recorder process ----------------------------------------------------- */
  requestRecorder(): void;
  startRecorder(): void;
  stopRecorder(): void;
  setStartWithWindows(enabled: boolean): void;
}

export function createBridge(transport: HostTransport = createTransport()): Bridge {
  const frameHandlers = new Set<(frame: FrameEvent) => void>();
  const messageHandlers = new Map<HostMessageType, Set<(message: HostMessage) => void>>();

  const safeCall = (fn: () => void, what: string): void => {
    try {
      fn();
    } catch (error) {
      console.error(`[screen-recall] ${what} handler threw`, error);
    }
  };

  transport.subscribe((event) => {
    if (isFrameEvent(event)) {
      for (const handler of [...frameHandlers]) safeCall(() => handler(event), 'frame');
      return;
    }
    const handlers = messageHandlers.get(event.type);
    if (!handlers || handlers.size === 0) return;
    for (const handler of [...handlers]) safeCall(() => handler(event), event.type);
  });

  const post = (command: OutgoingCommand): void => transport.post(command);

  return {
    kind: transport.kind,

    dispose() {
      frameHandlers.clear();
      messageHandlers.clear();
      transport.dispose();
    },

    onFrame(handler) {
      frameHandlers.add(handler);
      return () => {
        frameHandlers.delete(handler);
      };
    },

    on(type, handler) {
      let handlers = messageHandlers.get(type);
      if (!handlers) {
        handlers = new Set<(message: HostMessage) => void>();
        messageHandlers.set(type, handlers);
      }
      const bucket = handlers;
      const wrapped = (message: HostMessage): void => {
        handler(message as HostMessageOf<typeof type>);
      };
      bucket.add(wrapped);
      return () => {
        bucket.delete(wrapped);
      };
    },

    requestDays: () => post({ cmd: 'days' }),
    requestReplays: (day) => post({ cmd: 'replays', day }),
    openDay: (day) => post({ cmd: 'open', day }),

    play: () => post({ cmd: 'play' }),
    pause: () => post({ cmd: 'pause' }),
    togglePlay: () => post({ cmd: 'togglePlay' }),
    seek: (positionUs) => post({ cmd: 'seek', positionUs }),
    seekProgress: (progress) => post({ cmd: 'seekProgress', progress }),
    setSpeed: (value) => post({ cmd: 'speed', value }),
    step: (direction) => post({ cmd: 'step', direction }),
    skipIdle: () => post({ cmd: 'skipIdle' }),

    requestIdles: (minGapUs) => post(minGapUs === undefined ? { cmd: 'idles' } : { cmd: 'idles', minGapUs }),
    requestSpans: () => post({ cmd: 'spans' }),

    savePng: () => post({ cmd: 'savePng' }),
    requestStatus: () => post({ cmd: 'status' }),
    requestConfig: () => post({ cmd: 'config.get' }),
    saveConfig: (config) => post({ cmd: 'config.set', config }),
    pauseCapture: () => post({ cmd: 'capture.pause' }),
    resumeCapture: () => post({ cmd: 'capture.resume' }),
    purgeRecent: (minutes) => post({ cmd: 'purgeRecent', minutes }),
    prune: () => post({ cmd: 'prune' }),
    openStorage: () => post({ cmd: 'openStorage' }),
    refresh: () => post({ cmd: 'refresh' }),

    requestRecorder: () => post({ cmd: 'recorder.get' }),
    startRecorder: () => post({ cmd: 'recorder.start' }),
    stopRecorder: () => post({ cmd: 'recorder.stop' }),
    setStartWithWindows: (enabled) => post({ cmd: 'recorder.startup', enabled }),
  };
}
