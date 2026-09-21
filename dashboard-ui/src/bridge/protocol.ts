/**
 * The frozen wire protocol between the WPF/WebView2 host and this UI.
 *
 * Outgoing messages are JSON objects posted with `window.chrome.webview.postMessage`.
 * Incoming messages are JSON objects plus one special case: `frame`, which arrives on
 * the `sharedbufferreceived` event carrying raw RGBA pixels in a WebView2 shared buffer.
 *
 * Every timestamp on the wire is **microseconds since the Unix epoch, local wall clock**
 * (`µs / 1000 -> ms -> new Date(ms)`). Durations are microseconds as well. Day keys are
 * local calendar days formatted `YYYY-MM-DD`.
 *
 * Treat host payloads as untrusted: every field is typed optional where practical and the
 * UI renders "—" when a value is absent.
 */

/* -------------------------------------------------------------------------- */
/* Records                                                                     */
/* -------------------------------------------------------------------------- */

/** One recorded day as reported by `{cmd:'days'}`. */
export interface DayInfo {
  day: string;
  firstUs: number;
  lastUs: number;
  spanUs: number;
  logBytes: number;
  sessionBytes: number;
  entries: number;
  checkpoints: number;
  monitors: number;
}

/** One recorded stretch (log segment) from `{cmd:'replays', day}` / `{cmd:'open', day}`. */
export interface ReplayRow {
  fileName: string;
  startUs: number;
  endUs: number;
  durationUs: number;
  entryCount: number;
  distinctWindows: number;
  bytes: number;
}

/** Payload of `{type:'opened'}`: the day is open and its first frame follows. */
export interface OpenedSession {
  day: string;
  firstUs: number;
  lastUs: number;
  durationUs: number;
  checkpoints: number;
  width: number;
  height: number;
  replays: ReplayRow[];
}

/** Payload of `{type:'transport'}`, and the same fields repeated on every frame meta. */
export interface TransportState {
  playing: boolean;
  positionUs: number;
  durationUs: number;
  speed: number;
}

/** A quiet stretch of the day from `{cmd:'idles', minGapUs}`. */
export interface IdleSpan {
  startUs: number;
  endUs: number;
  durationUs: number;
}

/** A "the user was looking at this" stretch. Navigation metadata only. */
export interface FocusSpan {
  appName: string;
  windowTitle: string;
  startUs: number;
  endUs: number;
  count: number;
}

/**
 * Live capture-service status (`{cmd:'status'}`).
 *
 * The host sends camelCase. A few aliases are accepted defensively (`uptime`,
 * `queueDepth`, `framesStored`) because the same values are mirrored by the service
 * status DTO; unknown extra fields are preserved by the index signature.
 */
export interface CaptureStatus {
  state?: string;
  paused?: boolean;
  uptimeSeconds?: number;
  uptime?: number;
  cpuPercent?: number;
  workingSetMb?: number;
  framesAcquired?: number;
  framesWithChanges?: number;
  framesStored?: number;
  framesSkippedExcluded?: number;
  tilesHashed?: number;
  tilesDeduped?: number;
  tilesStored?: number;
  logEntries?: number;
  assetBytesWritten?: number;
  averageFrameMs?: number;
  throttleLevel?: number;
  monitors?: number;
  foregroundApp?: string;
  lastError?: string | null;
  lastMaintenance?: string | null;
  storageRoot?: string;
  day?: string;
  freeDiskGb?: number;
  assetCountOnDisk?: number;
  assetQueueDepth?: number;
  queueDepth?: number;
  sessionBytes?: number;
  logZeroRecordFaults?: number;
  logExternalWriterDetected?: boolean;
  [key: string]: unknown;
}

export type FidelityMode = 'lossless' | 'archive' | 'balanced';

/** Payload of `{cmd:'config.get'}` / `{cmd:'config.set', config}`. */
export interface RecallConfig {
  storagePath: string;
  retentionDays: number;
  tileSize: number;
  checkpointSeconds: number;
  fidelityMode: FidelityMode;
  captureAllMonitors: boolean;
  excludedProcesses: string[];
  excludedTitlePatterns: string[];
  /** Reserved: encryption at rest is **not implemented** by the service yet. */
  encryptionAtRest: boolean;
  [key: string]: unknown;
}

/** Payload of `{type:'pruned'}`. */
export interface PruneResult {
  daysDeleted?: number;
  assetsDeleted?: number;
  bytesReclaimed?: number;
  sessionBytesDeleted?: number;
  assetBytesDeleted?: number;
  days?: string[];
  message?: string;
}

/* -------------------------------------------------------------------------- */
/* Frames                                                                      */
/* -------------------------------------------------------------------------- */

/** `additionalData` of the WebView2 `sharedbufferreceived` event. */
export interface FrameMeta {
  type: 'frame';
  width: number;
  height: number;
  positionUs: number;
  durationUs: number;
  playing: boolean;
  speed: number;
  firstUs: number;
}

/**
 * A frame delivered to subscribers.
 *
 * `pixels` is a zero-copy `Uint8ClampedArray` view over the WebView2 shared buffer,
 * `width * height * 4` bytes, **RGBA byte order** (never swap channels). Handlers must
 * copy or draw it synchronously: the transport releases the buffer as soon as the last
 * subscriber returns.
 */
export interface FrameEvent {
  type: 'frame';
  meta: FrameMeta;
  pixels: Uint8ClampedArray;
}

/* -------------------------------------------------------------------------- */
/* Outgoing commands                                                           */
/* -------------------------------------------------------------------------- */

export type OutgoingCommand =
  | { cmd: 'days' }
  | { cmd: 'replays'; day: string }
  | { cmd: 'open'; day: string }
  | { cmd: 'play' }
  | { cmd: 'pause' }
  | { cmd: 'togglePlay' }
  | { cmd: 'seek'; positionUs: number }
  | { cmd: 'seekProgress'; progress: number }
  | { cmd: 'speed'; value: number }
  | { cmd: 'step'; direction: 1 | -1 }
  | { cmd: 'skipIdle' }
  | { cmd: 'idles'; minGapUs?: number }
  | { cmd: 'spans' }
  | { cmd: 'savePng' }
  | { cmd: 'status' }
  | { cmd: 'config.get' }
  | { cmd: 'config.set'; config: RecallConfig }
  | { cmd: 'capture.pause' }
  | { cmd: 'capture.resume' }
  | { cmd: 'purgeRecent'; minutes: number }
  | { cmd: 'prune' }
  | { cmd: 'openStorage' }
  | { cmd: 'recorder.get' }
  | { cmd: 'recorder.start' }
  | { cmd: 'recorder.stop' }
  | { cmd: 'recorder.startup'; enabled: boolean }
  | { cmd: 'refresh' };

export type OutgoingCommandName = OutgoingCommand['cmd'];

/* -------------------------------------------------------------------------- */
/* Incoming messages (frames excluded: those travel as shared buffers)          */
/* -------------------------------------------------------------------------- */

export type HostMessage =
  | {
      type: 'transport';
      playing?: boolean;
      positionUs?: number;
      durationUs?: number;
      speed?: number;
      firstUs?: number;
    }
  | { type: 'days'; days?: DayInfo[] }
  | { type: 'replays'; day?: string; checkpoints?: number; replays?: ReplayRow[] }
  | {
      type: 'opened';
      day?: string;
      firstUs?: number;
      lastUs?: number;
      durationUs?: number;
      checkpoints?: number;
      width?: number;
      height?: number;
      replays?: ReplayRow[];
    }
  | { type: 'status'; status?: CaptureStatus }
  | { type: 'config'; config?: RecallConfig }
  | { type: 'spans'; day?: string; spans?: FocusSpan[] }
  | { type: 'idles'; day?: string; spans?: IdleSpan[] }
  | { type: 'notice'; message?: string; level?: string }
  | { type: 'lightbox'; message?: string; caption?: string; url?: string }
  | ({ type: 'pruned' } & PruneResult)
  | {
      type: 'recorder';
      running?: boolean;
      startWithWindows?: boolean;
      executable?: string | null;
      startupCommand?: string | null;
      message?: string;
    }
  | { type: 'error'; message?: string };

/** Anything a transport can hand to the bridge. */
export type HostEvent = FrameEvent | HostMessage;

export type HostMessageType = HostMessage['type'];

export type HostMessageOf<T extends HostMessageType> = Extract<HostMessage, { type: T }>;

export type HostListener<T extends HostMessageType> = (message: HostMessageOf<T>) => void;

/** Narrowing helper: separates a pixel payload from a JSON message. */
export function isFrameEvent(event: HostEvent): event is FrameEvent {
  return event.type === 'frame' && 'pixels' in event && 'meta' in event;
}

/* -------------------------------------------------------------------------- */
/* Constants driven by the protocol                                            */
/* -------------------------------------------------------------------------- */

export const SPEED_OPTIONS: readonly number[] = [0.5, 1, 2, 4, 8];

/** Choices for `{cmd:'idles', minGapUs}` — a "quiet stretch" is at least this long. */
export const IDLE_GAP_OPTIONS: readonly { label: string; us: number }[] = [
  { label: '1 min', us: 60_000_000 },
  { label: '2 min', us: 120_000_000 },
  { label: '5 min', us: 300_000_000 },
  { label: '10 min', us: 600_000_000 },
  { label: '30 min', us: 1_800_000_000 },
];

export const DEFAULT_IDLE_GAP_US = 120_000_000;

/** Status poll cadence requested by the settings view (2 s). */
export const STATUS_POLL_MS = 2000;
