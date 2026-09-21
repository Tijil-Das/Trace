import type {
  CaptureStatus,
  FocusSpan,
  FrameEvent,
  FrameMeta,
  HostEvent,
  IdleSpan,
  OutgoingCommand,
  RecallConfig,
  ReplayRow,
} from './protocol';
import type { HostTransport } from './transport';

/**
 * The dev fallback: a fake recorder.
 *
 * This exists so `npm run dev` and a plain browser give a fully working dashboard — days to pick, stretches to
 * jump between, a moving picture, live status and a settings form — without the WPF host. Nothing here is used
 * when the app runs inside WebView2: `createTransport()` prefers the real host whenever one is present.
 *
 * The picture is drawn into an offscreen canvas on a timer, so the frame path (an `ImageData`-shaped RGBA
 * buffer) is exercised exactly as the real shared buffer would be.
 */

export const MOCK_FRAME_WIDTH = 960;
export const MOCK_FRAME_HEIGHT = 540;

const FRAME_INTERVAL_MS = 40;

/** Repeats the recorder's day layout: today plus two earlier days, one of them split in two stretches. */
const MOCK_DAYS = [
  { day: '2026-09-21', firstUs: 0, spanUs: 3 * 3600_000_000, segments: 2 },
  { day: '2026-09-20', firstUs: 0, spanUs: 9 * 3600_000_000, segments: 1 },
  { day: '2026-09-18', firstUs: 0, spanUs: 5400_000_000, segments: 1 },
];

function mockFirstUs(day: string): number {
  const entry = MOCK_DAYS.find((candidate) => candidate.day === day);
  const dayStart = new Date(`${day}T00:00:00`).getTime();
  return (dayStart + (entry ? 9 * 3600_000 : 3600_000)) * 1000;
}

function clockText(us: number): string {
  const date = new Date(Math.round(us / 1000));
  const pad = (value: number, width = 2): string => String(value).padStart(width, '0');
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`;
}

function dayText(day: string): string {
  const date = new Date(`${day}T12:00:00`);
  if (Number.isNaN(date.getTime())) return day;
  const weekdays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  return `${weekdays[date.getDay()]} ${date.getDate()} ${months[date.getMonth()]} ${date.getFullYear()}`;
}

interface MockState {
  day: string;
  firstUs: number;
  lastUs: number;
  positionUs: number;
  playing: boolean;
  speed: number;
  nextId: number;
  config: RecallConfig;
  timer: number | null;
  recorderRunning: boolean;
  startWithWindows: boolean;
}

export function createMockTransport(): HostTransport {
  const listeners = new Set<(event: HostEvent) => void>();
  const canvas = document.createElement('canvas');
  canvas.width = MOCK_FRAME_WIDTH;
  canvas.height = MOCK_FRAME_HEIGHT;
  const context = canvas.getContext('2d', { alpha: false });

  const state: MockState = {
    day: MOCK_DAYS[0]!.day,
    firstUs: mockFirstUs(MOCK_DAYS[0]!.day),
    lastUs: mockFirstUs(MOCK_DAYS[0]!.day) + MOCK_DAYS[0]!.spanUs,
    positionUs: mockFirstUs(MOCK_DAYS[0]!.day),
    playing: false,
    speed: 1,
    nextId: 1,
    config: {
      version: 1,
      storagePath: '%ProgramData%\\ScreenRecall\\data',
      retentionDays: 30,
      tileSize: 64,
      checkpointSeconds: 180,
      fidelityMode: 'lossless',
      captureAllMonitors: true,
      excludedProcesses: ['KeePassXC.exe', '1Password.exe', 'Bitwarden.exe'],
      excludedTitlePatterns: ['*incognito*', '*InPrivate*', '*Private Browsing*'],
      encryptionAtRest: false,
      idlePollMs: 1000,
      burstPollMs: 0,
      maxDailyMegabytes: 2048,
      minFreeDiskMegabytes: 2048,
      pauseOnBattery: false,
      paused: false,
    },
    timer: null,
    recorderRunning: true,
    startWithWindows: false,
  };

  const emit = (event: HostEvent): void => {
    for (const listener of [...listeners]) {
      try {
        listener(event);
      } catch (error) {
        console.error('[screen-recall] mock listener failed', error);
      }
    }
  };

  const duration = (): number => state.lastUs - state.firstUs;

  const emitTransport = (): void => {
    emit({
      type: 'transport',
      playing: state.playing,
      positionUs: state.positionUs,
      durationUs: duration(),
      speed: state.speed,
      firstUs: state.firstUs,
    });
  };

  /** The mock recorder always claims to be findable; only "running" and the sign-in flag change. */
  const emitRecorder = (message?: string): void => {
    const executable = 'C:\\dev-mock\\ScreenRecall.CaptureService.exe';
    emit({
      type: 'recorder',
      running: state.recorderRunning,
      startWithWindows: state.startWithWindows,
      executable,
      startupCommand: state.startWithWindows ? `"${executable}" --console` : null,
      message,
    });
  };

  const mockReplays = (day: string): ReplayRow[] => {
    const entry = MOCK_DAYS.find((candidate) => candidate.day === day) ?? MOCK_DAYS[0]!;
    const first = mockFirstUs(entry.day);
    const slice = Math.floor(entry.spanUs / entry.segments);
    const rows: ReplayRow[] = [];
    for (let index = 0; index < entry.segments; index++) {
      const start = first + slice * index;
      const end = first + slice * (index + 1) - 90_000_000;
      rows.push({
        fileName: index === 0 ? 'log.bin' : `log.${String(index).padStart(4, '0')}.bin`,
        startUs: start,
        endUs: end,
        durationUs: Math.max(0, end - start),
        entryCount: 1400 + index * 620,
        distinctWindows: 3 + index,
        bytes: 1_820_000 + index * 240_000,
      });
    }

    return rows;
  };

  /** Two quiet stretches, so the timeline has bands and "skip quiet" has somewhere to go. */
  const mockIdles = (): IdleSpan[] => {
    const base = state.firstUs;
    const spans: IdleSpan[] = [];
    const first = { startUs: base + 600_000_000, endUs: base + 1500_000_000 };
    const second = { startUs: base + 3000_000_000, endUs: base + 4800_000_000 };
    for (const span of [first, second]) {
      if (span.endUs <= state.lastUs) {
        spans.push({ ...span, durationUs: span.endUs - span.startUs });
      }
    }

    return spans;
  };

  const mockSpans = (): FocusSpan[] => {
    const base = state.firstUs;
    const apps: [string, string][] = [
      ['Code.exe', 'trace — storage-lib'],
      ['msedge.exe', 'Quite OK Image format'],
      ['explorer.exe', 'dev-data'],
      ['Code.exe', 'trace — player-lib'],
    ];
    return apps.map(([appName, windowTitle], index) => {
      const startUs = base + index * 900_000_000;
      return { appName, windowTitle, startUs, endUs: startUs + 420_000_000, count: 1 };
    });
  };

  const drawFrame = (): FrameEvent | null => {
    if (!context) return null;
    const elapsed = state.positionUs - state.firstUs;

    context.fillStyle = '#0b0f14';
    context.fillRect(0, 0, canvas.width, canvas.height);

    context.fillStyle = '#131b27';
    context.fillRect(48, 40, canvas.width - 96, canvas.height - 140);
    context.strokeStyle = '#223044';
    context.lineWidth = 1;
    context.strokeRect(48.5, 40.5, canvas.width - 97, canvas.height - 141);

    context.fillStyle = '#e6edf3';
    context.font = '600 22px "Segoe UI", system-ui, sans-serif';
    context.fillText(`mock recording — ${dayText(state.day)}`, 72, 88);

    context.fillStyle = '#9aa8bb';
    context.font = '16px ui-monospace, Consolas, monospace';
    context.fillText(`${clockText(state.positionUs)}   +${(elapsed / 1_000_000).toFixed(1)}s`, 72, 116);

    // Motion keyed off the playhead, not off wall-clock: seeking back shows the same picture twice.
    const travel = Math.max(1, canvas.width - 340);
    const x = 72 + ((elapsed / 40_000) % travel);
    context.fillStyle = '#1f6feb';
    context.fillRect(x, 176, 180, 92);
    context.fillStyle = '#4c9aff';
    context.fillRect(x, 176, 12, 92);

    for (const span of mockIdles()) {
      if (state.positionUs >= span.startUs && state.positionUs <= span.endUs) {
        context.fillStyle = '#d29922';
        context.font = '15px "Segoe UI", system-ui, sans-serif';
        context.fillText('quiet stretch — nothing changed here', 72, canvas.height - 72);
      }
    }

    context.fillStyle = '#6d7c90';
    context.font = '13px "Segoe UI", system-ui, sans-serif';
    context.fillText('dev mock (no host attached)', 72, canvas.height - 46);

    const image = context.getImageData(0, 0, canvas.width, canvas.height);
    const meta: FrameMeta = {
      type: 'frame',
      width: canvas.width,
      height: canvas.height,
      positionUs: state.positionUs,
      durationUs: duration(),
      playing: state.playing,
      speed: state.speed,
      firstUs: state.firstUs,
    };

    return { type: 'frame', meta, pixels: image.data };
  };

  const emitFrame = (): void => {
    const frame = drawFrame();
    if (frame) emit(frame);
  };

  const stopTimer = (): void => {
    if (state.timer === null) return;
    window.clearInterval(state.timer);
    state.timer = null;
  };

  const tick = (): void => {
    if (!state.playing) return;
    state.positionUs = Math.min(state.lastUs, state.positionUs + Math.round(FRAME_INTERVAL_MS * 1000 * state.speed));
    if (state.positionUs >= state.lastUs) {
      state.playing = false;
      stopTimer();
    }

    emitFrame();
    emitTransport();
  };

  const startTimer = (): void => {
    if (state.timer !== null) return;
    state.timer = window.setInterval(tick, FRAME_INTERVAL_MS);
  };

  const mockStatus = (): CaptureStatus => ({
    state: state.playing ? 'capturing' : 'idle',
    paused: false,
    uptimeSeconds: 412.5,
    cpuPercent: 3.4,
    workingSetMb: 58.2,
    framesAcquired: 184_320,
    framesWithChanges: 92_144,
    tilesHashed: 2_418_002,
    tilesStored: 121_361,
    tilesDeduped: 2_296_641,
    logEntries: 92_144,
    assetCountOnDisk: 121_361,
    assetBytesOnDisk: 202_400_000,
    assetBytesWritten: 202_400_000,
    sessionBytes: 3_900_000,
    freeDiskGb: 189.4,
    canvasTiles: 264,
    assetQueueDepth: 0,
    monitors: 1,
    storageRoot: '%ProgramData%\\ScreenRecall\\data',
    day: state.day,
    foregroundApp: 'Code.exe',
    lastMaintenance: '2026-09-21 09:00:12 — prune ok',
    lastError: null,
    throttleLevel: 0,
  });

  const openDay = (day: string): void => {
    const entry = MOCK_DAYS.find((candidate) => candidate.day === day) ?? MOCK_DAYS[0]!;
    state.day = entry.day;
    state.firstUs = mockFirstUs(entry.day);
    state.lastUs = state.firstUs + entry.spanUs;
    state.positionUs = state.firstUs;
    state.playing = false;
    stopTimer();

    emit({
      type: 'opened',
      day: state.day,
      firstUs: state.firstUs,
      lastUs: state.lastUs,
      durationUs: duration(),
      checkpoints: 12,
      width: MOCK_FRAME_WIDTH,
      height: MOCK_FRAME_HEIGHT,
      replays: mockReplays(state.day),
    });
    emitTransport();
    emitFrame();
    emit({ type: 'idles', day: state.day, spans: mockIdles() });
    emit({ type: 'spans', spans: mockSpans() });
  };

  const dayRows = () =>
    MOCK_DAYS.map((entry) => ({
      day: entry.day,
      firstUs: mockFirstUs(entry.day),
      lastUs: mockFirstUs(entry.day) + entry.spanUs,
      spanUs: entry.spanUs,
      logBytes: 3_900_000,
      sessionBytes: 4_100_000,
      entries: 92_144,
      checkpoints: 12,
      monitors: 1,
    }));

  const post = (command: OutgoingCommand): void => {
    switch (command.cmd) {
      case 'days':
        emit({ type: 'days', days: dayRows() });
        break;
      case 'replays':
        emit({ type: 'replays', day: command.day, checkpoints: 12, replays: mockReplays(command.day) });
        break;
      case 'open':
        openDay(command.day);
        break;
      case 'play':
        state.playing = true;
        startTimer();
        emitTransport();
        emitFrame();
        break;
      case 'pause':
        state.playing = false;
        stopTimer();
        emitTransport();
        break;
      case 'togglePlay':
        state.playing = !state.playing;
        if (state.playing) startTimer();
        else stopTimer();
        emitTransport();
        break;
      case 'seek':
        state.positionUs = Math.min(state.lastUs, Math.max(state.firstUs, command.positionUs));
        emitFrame();
        emitTransport();
        break;
      case 'seekProgress':
        state.positionUs = state.firstUs + Math.round(Math.min(1, Math.max(0, command.progress)) * duration());
        emitFrame();
        emitTransport();
        break;
      case 'speed':
        state.speed = command.value;
        emitTransport();
        break;
      case 'step':
        state.playing = false;
        stopTimer();
        state.positionUs = Math.min(
          state.lastUs,
          Math.max(state.firstUs, state.positionUs + (command.direction >= 0 ? 1 : -1) * 1_000_000),
        );
        emitFrame();
        emitTransport();
        break;
      case 'skipIdle': {
        const span = mockIdles().find((candidate) => candidate.endUs > state.positionUs);
        if (span) {
          state.positionUs = span.endUs;
          emitFrame();
          emitTransport();
          emit({ type: 'notice', message: `skipped ${(span.durationUs / 1_000_000).toFixed(1)}s with no change` });
        } else {
          emit({ type: 'notice', message: 'no quiet stretch ahead' });
        }
        break;
      }
      case 'idles':
        emit({ type: 'idles', day: state.day, spans: mockIdles() });
        break;
      case 'spans':
        emit({ type: 'spans', spans: mockSpans() });
        break;
      case 'savePng':
        emit({ type: 'notice', message: 'dev mock: no PNG written (no host attached)' });
        break;
      case 'status':
        emit({ type: 'status', status: mockStatus() });
        break;
      case 'config.get':
        emit({ type: 'config', config: { ...state.config } });
        break;
      case 'config.set':
        state.config = { ...command.config };
        emit({ type: 'config', config: { ...state.config } });
        emit({ type: 'notice', message: 'dev mock: settings kept in memory' });
        break;
      case 'capture.pause':
      case 'capture.resume':
        emit({
          type: 'notice',
          message: command.cmd === 'capture.pause' ? 'capture paused (mock)' : 'capture resumed (mock)',
        });
        emit({ type: 'status', status: mockStatus() });
        break;
      case 'purgeRecent':
      case 'prune':
        emit({
          type: 'pruned',
          daysDeleted: command.cmd === 'prune' ? 2 : 0,
          assetsDeleted: 4_120,
          bytesReclaimed: 18_400_000,
          message: command.cmd === 'prune' ? 'mock prune removed 2 days' : 'mock purge removed the last stretch',
        });
        break;
      case 'openStorage':
        emit({ type: 'notice', message: 'dev mock: no folder to open' });
        break;
      case 'recorder.get':
        emitRecorder();
        break;
      case 'recorder.start':
        state.recorderRunning = true;
        emitRecorder('dev mock: recorder started');
        break;
      case 'recorder.stop':
        state.recorderRunning = false;
        emitRecorder('dev mock: recorder stopped');
        break;
      case 'recorder.startup':
        state.startWithWindows = command.enabled;
        emitRecorder(
          command.enabled ? 'the recorder will start when you sign in' : 'the recorder will no longer start by itself',
        );
        break;
      case 'refresh':
        emit({ type: 'days', days: dayRows() });
        emit({ type: 'status', status: mockStatus() });
        break;
      default:
        emit({ type: 'error', message: `dev mock does not implement '${(command as { cmd?: string }).cmd ?? '?'}'` });
        break;
    }
  };

  return {
    kind: 'mock',
    post,
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    dispose() {
      stopTimer();
      listeners.clear();
    },
  };
}
