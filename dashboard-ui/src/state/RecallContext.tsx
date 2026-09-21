import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactElement,
  type ReactNode,
} from 'react';
import { createBridge, type Bridge } from '../bridge/bridge';
import {
  DEFAULT_IDLE_GAP_US,
  STATUS_POLL_MS,
  type CaptureStatus,
  type DayInfo,
  type FocusSpan,
  type IdleSpan,
  type OpenedSession,
  type RecallConfig,
  type ReplayRow,
} from '../bridge/protocol';
import { clamp } from '../lib/format';

export type Section = 'revisit' | 'settings';
export type LoadState = 'loading' | 'ready' | 'empty' | 'error';

export interface Toast {
  id: number;
  kind: 'info' | 'success' | 'warn' | 'error';
  message: string;
  detail?: string;
  at: number;
}

export interface FrameSize {
  width: number;
  height: number;
}

/**
 * State of the recorder process itself, as opposed to the capture service's own status: whether a recorder
 * process is alive, and whether one is registered to start at sign-in.
 */
export interface RecorderState {
  running: boolean;
  startWithWindows: boolean;
  executable: string | null;
  startupCommand: string | null;
}

/** Playback anchor: a host-reported position plus the moment it was reported. */
interface Anchor {
  positionUs: number;
  atMs: number;
  playing: boolean;
  speed: number;
  durationUs: number;
  firstUs: number;
}

export interface RecallContextValue {
  bridge: Bridge;
  mode: 'webview' | 'mock';

  /* days + selected day */
  days: DayInfo[];
  daysState: LoadState;
  daysError: string | null;
  selectedDay: string | null;

  /* opened session */
  session: OpenedSession | null;
  sessionState: LoadState;
  sessionError: string | null;

  /* lists for the selected day */
  replays: ReplayRow[];
  replaysState: LoadState;
  replayCheckpoints: number | null;
  idles: IdleSpan[];
  idlesState: LoadState;
  idleMinGapUs: number;
  setIdleMinGapUs(us: number): void;
  spans: FocusSpan[];
  spansState: LoadState;

  /* playback */
  positionUs: number;
  firstUs: number;
  lastUs: number;
  durationUs: number;
  playing: boolean;
  speed: number;
  scrubbing: boolean;
  frameSize: FrameSize | null;
  lastFrameUs: number | null;

  /* capture service */
  status: CaptureStatus | null;
  statusState: LoadState;
  statusAt: number | null;
  config: RecallConfig | null;
  configState: LoadState;
  configAt: number | null;

  /* feedback */
  toasts: Toast[];
  lastError: string | null;
  dismissToast(id: number): void;

  /* actions */
  refresh(): void;
  openDay(day: string): void;
  play(): void;
  pause(): void;
  togglePlay(): void;
  setSpeed(value: number): void;
  step(direction: 1 | -1): void;
  skipIdle(): void;
  seekToProgress(progress: number): void;
  beginScrub(progress: number): void;
  updateScrub(progress: number): void;
  endScrub(progress: number): void;
  savePng(): void;
  pauseCapture(): void;
  resumeCapture(): void;
  purgeRecent(minutes: number): void;
  prune(): void;
  openStorage(): void;
  saveConfig(config: RecallConfig): void;
  reloadConfig(): void;

  /* recorder process */
  recorder: RecorderState | null;
  requestRecorder(): void;
  startRecorder(): void;
  stopRecorder(): void;
  setStartWithWindows(enabled: boolean): void;
}

const RecallContext = createContext<RecallContextValue | null>(null);

const EMPTY_ANCHOR: Anchor = {
  positionUs: 0,
  atMs: 0,
  playing: false,
  speed: 1,
  durationUs: 0,
  firstUs: 0,
};

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

/** Cadence at which host-driven playhead changes are mirrored into React state. */
const POSITION_SYNC_MS = 200;
/** How long to wait for a reply before calling it a failure. */
const DAYS_TIMEOUT_MS = 8000;
const SESSION_TIMEOUT_MS = 10_000;
const TOAST_TTL_MS = 7000;

/* -------------------------------------------------------------------------- */
/* Provider                                                                    */
/* -------------------------------------------------------------------------- */

export function RecallProvider({ children }: { children: ReactNode }): ReactElement {
  // Deliberately not wrapped in <StrictMode>: the dev-fallback host owns real timers and
  // pixel buffers, so React's double-mount probe would run two playback loops at once.
  const [bridge] = useState<Bridge>(() => createBridge());
  const mode: 'webview' | 'mock' = bridge.kind === 'webview' ? 'webview' : 'mock';

  const [days, setDays] = useState<DayInfo[]>([]);
  const [daysState, setDaysState] = useState<LoadState>('loading');
  const [daysError, setDaysError] = useState<string | null>(null);
  const [selectedDay, setSelectedDay] = useState<string | null>(null);

  const [session, setSession] = useState<OpenedSession | null>(null);
  const [sessionState, setSessionState] = useState<LoadState>('loading');
  const [sessionError, setSessionError] = useState<string | null>(null);

  const [replays, setReplays] = useState<ReplayRow[]>([]);
  const [replaysState, setReplaysState] = useState<LoadState>('loading');
  const [replayCheckpoints, setReplayCheckpoints] = useState<number | null>(null);
  const [idles, setIdles] = useState<IdleSpan[]>([]);
  const [idlesState, setIdlesState] = useState<LoadState>('loading');
  const [idleMinGapUs, setIdleMinGapUsState] = useState<number>(DEFAULT_IDLE_GAP_US);
  const [spans, setSpans] = useState<FocusSpan[]>([]);
  const [spansState, setSpansState] = useState<LoadState>('loading');

  const [anchor, setAnchor] = useState<Anchor>(EMPTY_ANCHOR);
  const [dragProgress, setDragProgress] = useState<number | null>(null);
  const [frameSize, setFrameSize] = useState<FrameSize | null>(null);
  const [lastFrameUs, setLastFrameUs] = useState<number | null>(null);

  const [status, setStatus] = useState<CaptureStatus | null>(null);
  const [statusState, setStatusState] = useState<LoadState>('loading');
  const [statusAt, setStatusAt] = useState<number | null>(null);
  const [config, setConfig] = useState<RecallConfig | null>(null);
  const [configState, setConfigState] = useState<LoadState>('loading');
  const [configAt, setConfigAt] = useState<number | null>(null);

  const [toasts, setToasts] = useState<Toast[]>([]);
  const [lastError, setLastError] = useState<string | null>(null);
  const [recorder, setRecorder] = useState<RecorderState | null>(null);

  const anchorRef = useRef<Anchor>(EMPTY_ANCHOR);
  const lastFrameRef = useRef<number | null>(null);
  const draggingRef = useRef(false);
  const selectedDayRef = useRef<string | null>(null);
  const idleGapRef = useRef<number>(DEFAULT_IDLE_GAP_US);
  const sessionTimerRef = useRef<number | null>(null);
  const lastSyncRef = useRef(0);
  const lastToastRef = useRef<{ message: string; at: number }>({ message: '', at: 0 });
  const toastIdRef = useRef(1);
  const openDayRef = useRef<(day: string) => void>(() => undefined);
  /** Set while a day switch is in flight; frames arriving before `opened` are dropped. */
  const openPendingRef = useRef<string | null>(null);

  const addToast = useCallback((kind: Toast['kind'], message: string, detail?: string) => {
    const now = Date.now();
    if (lastToastRef.current.message === message && now - lastToastRef.current.at < 1200) return;
    lastToastRef.current = { message, at: now };
    const id = toastIdRef.current;
    toastIdRef.current += 1;
    setToasts((prev) => [...prev.slice(-4), { id, kind, message, detail, at: now }]);
    window.setTimeout(() => setToasts((prev) => prev.filter((toast) => toast.id !== id)), TOAST_TTL_MS);
  }, []);

  const dismissToast = useCallback((id: number) => {
    setToasts((prev) => prev.filter((toast) => toast.id !== id));
  }, []);

  const clearSessionTimer = useCallback(() => {
    if (sessionTimerRef.current !== null) {
      window.clearTimeout(sessionTimerRef.current);
      sessionTimerRef.current = null;
    }
  }, []);

  /**
   * Mirrors the host's playback clock into state.
   *
   * Frames can arrive in bursts, so `throttle` keeps re-renders at `POSITION_SYNC_MS`
   * cadence while the refs stay exact — the canvas never goes through React. While the user
   * drags the scrubber, incoming positions are kept out of the displayed position entirely.
   */
  const applyTransport = useCallback(
    (
      msg: { playing?: boolean; positionUs?: number; durationUs?: number; speed?: number; firstUs?: number },
      throttle: boolean,
    ) => {
      const prev = anchorRef.current;
      const next: Anchor = {
        positionUs: draggingRef.current || !isFiniteNumber(msg.positionUs) ? prev.positionUs : msg.positionUs,
        playing: typeof msg.playing === 'boolean' ? msg.playing : prev.playing,
        speed: isFiniteNumber(msg.speed) && msg.speed > 0 ? msg.speed : prev.speed,
        durationUs: isFiniteNumber(msg.durationUs) ? msg.durationUs : prev.durationUs,
        firstUs: isFiniteNumber(msg.firstUs) ? msg.firstUs : prev.firstUs,
        atMs: performance.now(),
      };
      anchorRef.current = next;
      const now = performance.now();
      if (throttle && now - lastSyncRef.current < POSITION_SYNC_MS) return;
      lastSyncRef.current = now;
      setAnchor(next);
      setLastFrameUs(lastFrameRef.current);
    },
    [],
  );

  /* ------------------------------------------------------------------------ */
  /* Host message routing                                                      */
  /* ------------------------------------------------------------------------ */

  useEffect(() => {
    const offs: (() => void)[] = [];

    offs.push(
      bridge.on('days', (msg) => {
        const list = Array.isArray(msg.days) ? msg.days : [];
        setDays(list);
        setDaysError(null);
        setDaysState(list.length === 0 ? 'empty' : 'ready');
        const current = selectedDayRef.current;
        const stillPresent = current !== null && list.some((day) => day.day === current);
        const first = list[0];
        if (!stillPresent && first) openDayRef.current(first.day);
      }),
    );

    offs.push(
      bridge.on('replays', (msg) => {
        if (msg.day && selectedDayRef.current && msg.day !== selectedDayRef.current) return;
        const list = Array.isArray(msg.replays) ? msg.replays : [];
        setReplays(list);
        setReplayCheckpoints(isFiniteNumber(msg.checkpoints) ? msg.checkpoints : null);
        setReplaysState(list.length === 0 ? 'empty' : 'ready');
      }),
    );

    offs.push(
      bridge.on('opened', (msg) => {
        clearSessionTimer();
        openPendingRef.current = null;
        const day = typeof msg.day === 'string' && msg.day.length > 0 ? msg.day : selectedDayRef.current ?? '';
        const firstUs = isFiniteNumber(msg.firstUs) ? msg.firstUs : 0;
        const durationUs = isFiniteNumber(msg.durationUs) ? msg.durationUs : 0;
        const lastUs = isFiniteNumber(msg.lastUs) ? msg.lastUs : firstUs + durationUs;
        const width = isFiniteNumber(msg.width) ? msg.width : 0;
        const height = isFiniteNumber(msg.height) ? msg.height : 0;
        const list = Array.isArray(msg.replays) ? msg.replays : [];
        setSelectedDay(day);
        selectedDayRef.current = day;
        setSession({
          day,
          firstUs,
          lastUs,
          durationUs,
          checkpoints: isFiniteNumber(msg.checkpoints) ? msg.checkpoints : 0,
          width,
          height,
          replays: list,
        });
        setSessionState(durationUs > 0 ? 'ready' : 'empty');
        setSessionError(null);
        setReplays(list);
        setReplaysState(list.length === 0 ? 'empty' : 'ready');
        setFrameSize(width > 0 && height > 0 ? { width, height } : null);
        applyTransport({ positionUs: firstUs, durationUs, firstUs }, false);
      }),
    );

    offs.push(
      bridge.on('transport', (msg) => {
        applyTransport(msg, false);
      }),
    );

    offs.push(
      bridge.onFrame((frame) => {
        // Frames belong to the day that is open. Anything arriving mid-switch is stale.
        if (openPendingRef.current !== null) return;
        const { meta } = frame;
        const width = Math.round(meta.width);
        const height = Math.round(meta.height);
        if (width <= 0 || height <= 0) return;
        setFrameSize((prev) => (prev && prev.width === width && prev.height === height ? prev : { width, height }));
        lastFrameRef.current = meta.positionUs;
        applyTransport(
          {
            playing: meta.playing,
            positionUs: meta.positionUs,
            durationUs: meta.durationUs,
            speed: meta.speed,
            firstUs: meta.firstUs,
          },
          true,
        );
      }),
    );

    offs.push(
      bridge.on('status', (msg) => {
        setStatus(msg.status ?? {});
        setStatusAt(Date.now());
        setStatusState(msg.status ? 'ready' : 'empty');
      }),
    );

    offs.push(
      bridge.on('config', (msg) => {
        setConfig(msg.config ?? null);
        setConfigAt(Date.now());
        setConfigState(msg.config ? 'ready' : 'empty');
      }),
    );

    offs.push(
      bridge.on('spans', (msg) => {
        const list = Array.isArray(msg.spans) ? msg.spans : [];
        setSpans(list);
        setSpansState(list.length === 0 ? 'empty' : 'ready');
      }),
    );

    offs.push(
      bridge.on('idles', (msg) => {
        const list = Array.isArray(msg.spans) ? msg.spans : [];
        setIdles(list);
        setIdlesState(list.length === 0 ? 'empty' : 'ready');
      }),
    );

    offs.push(
      bridge.on('notice', (msg) => {
        const kind = msg.level === 'warn' ? 'warn' : msg.level === 'success' ? 'success' : msg.level === 'error' ? 'error' : 'info';
        const text = msg.message && msg.message.trim().length > 0 ? msg.message : 'Host notice with no message.';
        addToast(kind, text);
      }),
    );

    offs.push(
      bridge.on('lightbox', (msg) => {
        addToast('info', msg.message ?? msg.caption ?? 'Host requested an image preview.', msg.url);
      }),
    );

    offs.push(
      bridge.on('pruned', (msg) => {
        const details: string[] = [];
        if (isFiniteNumber(msg.daysDeleted)) details.push(`${msg.daysDeleted} day(s) deleted`);
        if (isFiniteNumber(msg.assetsDeleted)) details.push(`${msg.assetsDeleted} asset(s) removed`);
        if (isFiniteNumber(msg.bytesReclaimed)) details.push(`${(msg.bytesReclaimed / 1024 / 1024).toFixed(1)} MB reclaimed`);
        addToast('success', msg.message ?? 'Prune finished.', details.length > 0 ? details.join(' · ') : undefined);
      }),
    );

    offs.push(
      bridge.on('recorder', (msg) => {
        setRecorder({
          running: msg.running === true,
          startWithWindows: msg.startWithWindows === true,
          executable: typeof msg.executable === 'string' ? msg.executable : null,
          startupCommand: typeof msg.startupCommand === 'string' ? msg.startupCommand : null,
        });
        if (typeof msg.message === 'string' && msg.message.length > 0) {
          addToast('info', msg.message);
          // Starting or stopping the recorder changes what `status` can even answer, so re-ask.
          bridge.requestStatus();
        }
      }),
    );

    offs.push(
      bridge.on('error', (msg) => {
        const text = msg.message && msg.message.trim().length > 0 ? msg.message : 'The host reported an unspecified error.';
        setLastError(text);
        addToast('error', text);
      }),
    );

    return () => {
      for (const off of offs) off();
    };
  }, [bridge, addToast, applyTransport, clearSessionTimer]);

  /* ------------------------------------------------------------------------ */
  /* Polling                                                                  */
  /* ------------------------------------------------------------------------ */

  useEffect(() => {
    // Days and status are "read when asked": nothing pushes them, so the dashboard asks once on mount and then
    // keeps the service status fresh on a slow timer.
    bridge.requestDays();
    bridge.requestStatus();
    bridge.requestConfig();
    bridge.requestRecorder();
    const timer = window.setInterval(() => bridge.requestStatus(), STATUS_POLL_MS);

    // Nothing in this UI should spin forever: if the day list never arrives, say so.
    const daysTimer = window.setTimeout(() => {
      setDaysState((prev) => (prev === 'loading' ? 'error' : prev));
      setDaysError('the host did not answer with a day list');
    }, DAYS_TIMEOUT_MS);

    return () => {
      window.clearInterval(timer);
      window.clearTimeout(daysTimer);
    };
  }, [bridge]);

  /* ------------------------------------------------------------------------ */
  /* Actions                                                                  */
  /* ------------------------------------------------------------------------ */

  const refresh = useCallback(() => {
    bridge.refresh();
    bridge.requestDays();
    bridge.requestStatus();
  }, [bridge]);

  const openDay = useCallback(
    (day: string) => {
      if (day.length === 0) return;
      openPendingRef.current = day;
      setSessionState('loading');
      setSessionError(null);
      setReplaysState('loading');
      setSpansState('loading');
      setIdlesState('loading');
      clearSessionTimer();
      bridge.openDay(day);

      // A day that never opens must not leave the stage spinning forever: say so, and let the user retry.
      sessionTimerRef.current = window.setTimeout(() => {
        if (openPendingRef.current !== day) return;
        openPendingRef.current = null;
        setSessionState('error');
        setSessionError(`the host did not open ${day} — is the capture service available?`);
      }, SESSION_TIMEOUT_MS);
    },
    [bridge, clearSessionTimer],
  );

  useEffect(() => {
    openDayRef.current = openDay;
  }, [openDay]);

  const play = useCallback(() => bridge.play(), [bridge]);
  const pause = useCallback(() => bridge.pause(), [bridge]);
  const togglePlay = useCallback(() => bridge.togglePlay(), [bridge]);
  const setSpeed = useCallback((value: number) => bridge.setSpeed(value), [bridge]);
  const step = useCallback((direction: 1 | -1) => bridge.step(direction), [bridge]);
  const skipIdle = useCallback(() => bridge.skipIdle(), [bridge]);
  const seekToProgress = useCallback((progress: number) => bridge.seekProgress(clamp(progress, 0, 1)), [bridge]);
  const savePng = useCallback(() => bridge.savePng(), [bridge]);
  const pauseCapture = useCallback(() => bridge.pauseCapture(), [bridge]);
  const resumeCapture = useCallback(() => bridge.resumeCapture(), [bridge]);
  const purgeRecent = useCallback((minutes: number) => bridge.purgeRecent(minutes), [bridge]);
  const prune = useCallback(() => bridge.prune(), [bridge]);
  const openStorage = useCallback(() => bridge.openStorage(), [bridge]);
  const requestRecorder = useCallback(() => bridge.requestRecorder(), [bridge]);
  const startRecorder = useCallback(() => bridge.startRecorder(), [bridge]);
  const stopRecorder = useCallback(() => bridge.stopRecorder(), [bridge]);
  const setStartWithWindows = useCallback((enabled: boolean) => bridge.setStartWithWindows(enabled), [bridge]);

  const saveConfig = useCallback(
    (next: RecallConfig) => {
      setConfigState('loading');
      bridge.saveConfig(next);
    },
    [bridge],
  );

  const reloadConfig = useCallback(() => {
    setConfigState('loading');
    bridge.requestConfig();
  }, [bridge]);

  const setIdleMinGapUs = useCallback(
    (us: number) => {
      idleGapRef.current = us;
      setIdleMinGapUsState(us);
      bridge.requestIdles(us);
    },
    [bridge],
  );

  /* ------------------------------------------------------------------------ */
  /* Scrubbing                                                                */
  /* ------------------------------------------------------------------------ */

  /** Latest drag value, so `endScrub` never depends on a stale render. */
  const dragProgressRef = useRef<number | null>(null);

  const beginScrub = useCallback((progress: number) => {
    draggingRef.current = true;
    dragProgressRef.current = clamp(progress, 0, 1);
    setDragProgress(dragProgressRef.current);
  }, []);

  const updateScrub = useCallback((progress: number) => {
    draggingRef.current = true;
    dragProgressRef.current = clamp(progress, 0, 1);
    setDragProgress(dragProgressRef.current);
  }, []);

  /** The scrub the user just finished. The argument is preferred; the ref is the fallback. */
  const endScrub = useCallback((progress?: number) => {
    const target = progress ?? dragProgressRef.current;
    draggingRef.current = false;
    dragProgressRef.current = null;
    setDragProgress(null);
    if (target !== null && target !== undefined) {
      bridge.seekProgress(clamp(target, 0, 1));
    }
  }, [bridge]);

  /* ------------------------------------------------------------------------ */
  /* Derived playback state                                                   */
  /* ------------------------------------------------------------------------ */

  const scrubbing = dragProgress !== null;
  const durationUs = anchor.durationUs;
  const firstUs = anchor.firstUs;
  const lastUs = firstUs + durationUs;
  const positionUs = scrubbing ? firstUs + (dragProgress ?? 0) * durationUs : anchor.positionUs;
  const playing = scrubbing ? false : anchor.playing;

  // Built fresh each render on purpose: this provider re-renders exactly when its own state changes, so a memo
  // would add a dependency list the size of the whole API without saving a consumer render.
  const value: RecallContextValue = {
    bridge,
    mode,
    days,
    daysState,
    daysError,
    selectedDay,
    session,
    sessionState,
    sessionError,
    replays,
    replaysState,
    replayCheckpoints,
    idles,
    idlesState,
    idleMinGapUs,
    setIdleMinGapUs,
    spans,
    spansState,
    positionUs,
    firstUs,
    lastUs,
    durationUs,
    playing,
    speed: anchor.speed,
    scrubbing,
    frameSize,
    lastFrameUs,
    status,
    statusState,
    statusAt,
    config,
    configState,
    configAt,
    toasts,
    lastError,
    dismissToast,
    refresh,
    openDay,
    play,
    pause,
    togglePlay,
    setSpeed,
    step,
    skipIdle,
    seekToProgress,
    beginScrub,
    updateScrub,
    endScrub,
    savePng,
    pauseCapture,
    resumeCapture,
    purgeRecent,
    prune,
    openStorage,
    saveConfig,
    reloadConfig,
    recorder,
    requestRecorder,
    startRecorder,
    stopRecorder,
    setStartWithWindows,
  };
  return <RecallContext.Provider value={value}>{children}</RecallContext.Provider>;
}

/**
 * Access the dashboard state. Throws outside the provider: a silently empty context renders a UI that only
 * looks broken, and the real mistake (a component mounted above the provider) would be much harder to find.
 */
export function useRecall(): RecallContextValue {
  const value = useContext(RecallContext);
  if (!value) {
    throw new Error('useRecall() must be used inside <RecallProvider>.');
  }

  return value;
}

