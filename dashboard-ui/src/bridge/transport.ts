import type { FrameEvent, FrameMeta, HostEvent, OutgoingCommand } from './protocol';
import { createMockTransport } from './mockHost';

export type TransportKind = 'webview' | 'mock';

/**
 * A byte pipe to the host process.
 *
 * Two implementations exist: the real WebView2 one (`window.chrome.webview`) and the
 * dev-fallback mock. Everything above this interface is host-agnostic.
 */
export interface HostTransport {
  readonly kind: TransportKind;
  /** Send a command. Never throws: failures surface as `{type:'error'}` events. */
  post(command: OutgoingCommand): void;
  subscribe(listener: (event: HostEvent) => void): () => void;
  dispose(): void;
}

/* -------------------------------------------------------------------------- */
/* WebView2 surface we depend on                                               */
/* -------------------------------------------------------------------------- */

export interface WebViewSharedBufferEvent {
  additionalData: FrameMeta;
  getBuffer(): ArrayBuffer;
}

export interface WebViewMessageEvent {
  data: unknown;
}

export interface WebViewHost {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: WebViewMessageEvent) => void): void;
  addEventListener(type: 'sharedbufferreceived', listener: (event: WebViewSharedBufferEvent) => void): void;
  removeEventListener(type: 'message', listener: (event: WebViewMessageEvent) => void): void;
  removeEventListener(type: 'sharedbufferreceived', listener: (event: WebViewSharedBufferEvent) => void): void;
  releaseBuffer(buffer: ArrayBuffer): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewHost };
  }
}

/** The real host bridge, or `undefined` in a plain browser / `npm run dev`. */
export function getWebViewHost(): WebViewHost | undefined {
  if (typeof window === 'undefined') return undefined;
  const host = window.chrome?.webview;
  return host && typeof host.postMessage === 'function' ? host : undefined;
}

/* -------------------------------------------------------------------------- */
/* Helpers                                                                     */
/* -------------------------------------------------------------------------- */

const KNOWN_MESSAGE_TYPES: ReadonlySet<string> = new Set([
  'transport',
  'days',
  'replays',
  'opened',
  'status',
  'config',
  'spans',
  'idles',
  'notice',
  'lightbox',
  'pruned',
  'recorder',
  'error',
]);

export function describeError(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (typeof error === 'string') return error;
  try {
    return JSON.stringify(error);
  } catch {
    return String(error);
  }
}

/**
 * Normalises whatever the host posted into a `HostEvent`. Accepts JSON strings and
 * structured-clone objects alike, and ignores unknown shapes.
 */
function normalizeHostEvent(raw: unknown): HostEvent | null {
  let value = raw;
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value) as unknown;
    } catch {
      return null;
    }
  }
  if (!value || typeof value !== 'object') return null;

  const type: unknown = (value as { type?: unknown }).type;
  if (typeof type !== 'string') return null;
  if (type === 'frame') return null; // pixels never arrive as JSON
  if (!KNOWN_MESSAGE_TYPES.has(type)) return null;
  // The host is trusted; consumers read every field defensively anyway.
  return value as HostEvent;
}

/* -------------------------------------------------------------------------- */
/* WebView2 transport                                                          */
/* -------------------------------------------------------------------------- */

export function createWebViewTransport(host: WebViewHost): HostTransport {
  const listeners = new Set<(event: HostEvent) => void>();

  const emit = (event: HostEvent): void => {
    for (const listener of [...listeners]) {
      try {
        listener(event);
      } catch (error) {
        // A subscriber throwing must not take the bridge down with it.
        console.error('[screen-recall] listener failed', error);
      }
    }
  };

  const onMessage = (event: WebViewMessageEvent): void => {
    const parsed = normalizeHostEvent(event.data);
    if (parsed) emit(parsed);
  };

  const onSharedBuffer = (event: WebViewSharedBufferEvent): void => {
    const buffer = event.getBuffer();
    try {
      const meta = event.additionalData;
      if (!meta || meta.type !== 'frame') return;
      const width = Number(meta.width);
      const height = Number(meta.height);
      const bytes = width * height * 4;
      if (!Number.isFinite(bytes) || bytes <= 0) return;
      if (buffer.byteLength < bytes) {
        emit({
          type: 'error',
          message: `Shared buffer holds ${buffer.byteLength} bytes but a ${width}x${height} frame needs ${bytes}.`,
        });
        return;
      }
      // Zero-copy view. Subscribers draw synchronously (putImageData copies out of it),
      // so the buffer is safe to release as soon as they return.
      const frame: FrameEvent = { type: 'frame', meta, pixels: new Uint8ClampedArray(buffer, 0, bytes) };
      emit(frame);
    } catch (error) {
      emit({ type: 'error', message: `Frame decode failed: ${describeError(error)}` });
    } finally {
      // Release immediately: the host reuses this buffer for the next frame, and holding
      // it would stall capture.
      try {
        host.releaseBuffer(buffer);
      } catch {
        /* already released by the host */
      }
    }
  };

  host.addEventListener('message', onMessage);
  host.addEventListener('sharedbufferreceived', onSharedBuffer);

  return {
    kind: 'webview',
    post(command) {
      try {
        host.postMessage(command);
      } catch (error) {
        emit({ type: 'error', message: `postMessage(${command.cmd}) failed: ${describeError(error)}` });
      }
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    dispose() {
      host.removeEventListener('message', onMessage);
      host.removeEventListener('sharedbufferreceived', onSharedBuffer);
      listeners.clear();
    },
  };
}

/* -------------------------------------------------------------------------- */
/* Selection                                                                   */
/* -------------------------------------------------------------------------- */

/**
 * Picks the WebView2 transport when hosted, and the built-in mock otherwise, so the UI
 * is fully usable from `npm run dev` and in a plain browser.
 */
export function createTransport(): HostTransport {
  const host = getWebViewHost();
  if (host) return createWebViewTransport(host);
  return createMockTransport();
}
