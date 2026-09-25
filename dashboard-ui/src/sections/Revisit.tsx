import { useCallback, useEffect, useRef, useState } from 'react';

import { IDLE_GAP_OPTIONS, SPEED_OPTIONS } from '../bridge/protocol';
import {
  fmtBytes,
  fmtClock,
  fmtCount,
  fmtDayLong,
  fmtDuration,
  fmtDurationClock,
  fmtSpeed,
  fmtWallClock,
  progressOf,
} from '../lib/format';
import { useRecall } from '../state/RecallContext';

/**
 * Revisit: pick a day, watch it back like a video, and see what was recorded.
 *
 * The frame itself is drawn by the canvas below straight from the host's shared buffer. Nothing here
 * animates pixels on a timer — a quiet stretch of the day sends no frames, and the last one simply stays on
 * screen, which is exactly what "hold the still frame" means in a player that reconstructs from a log.
 */
export function Revisit() {
  const { days, daysState, daysError, selectedDay, openDay } = useRecall();

  return (
    <div className="revisit">
      <aside className="panel panel--days">
        <header className="panel__head">
          <h2 className="panel__title">Recorded days</h2>
          <span className="panel__hint">{daysState === 'loading' ? 'loading…' : `${days.length}`}</span>
        </header>

        {daysState === 'error' ? (
          <p className="empty empty--error">{daysError ?? 'The host could not list recorded days.'}</p>
        ) : days.length === 0 ? (
          <p className="empty">
            {daysState === 'loading' ? 'Reading the store…' : 'Nothing has been recorded yet.'}
          </p>
        ) : (
          <ul className="daylist">
            {days.map((day) => (
              <li key={day.day}>
                <button
                  type="button"
                  className={`day ${selectedDay === day.day ? 'day--active' : ''}`}
                  onClick={() => openDay(day.day)}
                >
                  <span className="day__name">{fmtDayLong(day.day)}</span>
                  <span className="day__meta">
                    {day.firstUs > 0 ? `${fmtWallClock(day.firstUs)} – ${fmtWallClock(day.lastUs)}` : 'empty'}
                    {' · '}
                    {fmtDuration(day.spanUs)}
                  </span>
                  <span className="day__meta day__meta--dim">
                    {fmtCount(day.entries)} events · {fmtBytes(day.sessionBytes)} · {day.checkpoints} checkpoints
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </aside>

      <PlayerStage />

      <aside className="panel panel--side">
        <ReplayList />
        <FocusList />
      </aside>
    </div>
  );
}

/** How long the controls stay up in fullscreen with no mouse, click or key. Standard-player territory. */
const CHROME_IDLE_MS = 2600;

/**
 * The player: the reconstructed screen, its transport and its timeline — plus the mode that hands them the
 * whole screen.
 *
 * Fullscreen is asked for from the browser (`requestFullscreen` on this element) rather than faked with CSS,
 * because the WPF host follows it: WebView2 reports the fullscreen element, the shell drops its own chrome, and
 * the picture reaches the edges of the display. When the API is unavailable or the host declines the request,
 * the same layout is applied as an overlay inside the window, so the mode never silently does nothing.
 *
 * The controls behave the way a player's do: in fullscreen they fade out after a few idle seconds and come back
 * on the smallest sign of life — a mouse move, a click or a key — unless the pointer is parked on them. Leaving
 * fullscreen always brings them back.
 */
function PlayerStage() {
  const { session, sessionState, sessionError } = useRecall();
  const playerRef = useRef<HTMLElement | null>(null);
  const [fullscreen, setFullscreen] = useState(false);
  const [filling, setFilling] = useState(false);
  const [idle, setIdle] = useState(false);
  const hideTimer = useRef<number | null>(null);
  const overChrome = useRef(false);

  const immersive = fullscreen || filling;

  const stopHideTimer = useCallback(() => {
    if (hideTimer.current !== null) {
      window.clearTimeout(hideTimer.current);
      hideTimer.current = null;
    }
  }, []);

  /** The controls are on screen; start the countdown that takes them away again. */
  const wakeChrome = useCallback(() => {
    setIdle(false);
    stopHideTimer();

    // Parked on the controls: they stay until the pointer leaves. A player that hides the button you are
    // reaching for is a player nobody can drive.
    if (overChrome.current) {
      return;
    }

    hideTimer.current = window.setTimeout(() => {
      hideTimer.current = null;
      setIdle(true);
    }, CHROME_IDLE_MS);
  }, [stopHideTimer]);

  const holdChrome = useCallback(() => {
    overChrome.current = true;
    setIdle(false);
    stopHideTimer();
  }, [stopHideTimer]);

  const releaseChrome = useCallback(() => {
    overChrome.current = false;
    wakeChrome();
  }, [wakeChrome]);

  // Only fullscreen hides anything. Leaving it — by this handler, by Esc, or because the host went back to a
  // window — must never leave the player without a transport bar, so every path lands in this effect.
  useEffect(() => {
    if (!immersive) {
      stopHideTimer();
      setIdle(false);
      return;
    }

    wakeChrome();
    return stopHideTimer;
  }, [immersive, wakeChrome, stopHideTimer]);

  // A key press is a sign of life too: space, the arrows and the step keys all mean someone is driving.
  useEffect(() => {
    if (!immersive) return;

    const onKey = () => wakeChrome();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [immersive, wakeChrome]);

  // The browser is the authority on whether an element is fullscreen: Esc, a host that leaves on its own and
  // any window-manager gesture all arrive only as this event.
  useEffect(() => {
    const onChange = () => {
      const own = document.fullscreenElement !== null;
      setFullscreen(own);
      if (!own) setFilling(false);
    };

    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, []);

  const enterFullscreen = useCallback(async () => {
    const node = playerRef.current;
    if (!node || document.fullscreenElement) return;

    if (document.fullscreenEnabled && typeof node.requestFullscreen === 'function') {
      try {
        await node.requestFullscreen();
        setFilling(false);
        return;
      } catch {
        // WebView2 can refuse the request. The mode still has to work, so it falls through to the overlay.
      }
    }

    setFilling(true);
  }, []);

  const exitFullscreen = useCallback(async () => {
    if (document.fullscreenElement) {
      try {
        await document.exitFullscreen();
      } catch {
        // The browser got there first; the state follows on `fullscreenchange`.
      }
      return;
    }

    setFilling(false);
  }, []);

  const toggleFullscreen = useCallback(() => {
    if (!session) return; // nothing to watch yet
    void (immersive ? exitFullscreen() : enterFullscreen());
  }, [session, immersive, enterFullscreen, exitFullscreen]);

  // The app shell owns the keyboard and announces intent, exactly as it does for play/pause and stepping; the
  // mode that acts on fullscreen is the one whose state lives here. Esc is handled here too: browsers and
  // WebView2 leave fullscreen on their own, but the in-window overlay has no such binding.
  useEffect(() => {
    const onToggle = () => toggleFullscreen();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && immersive) {
        void exitFullscreen();
      }
    };

    window.addEventListener('screen-recall:toggle-fullscreen', onToggle);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('screen-recall:toggle-fullscreen', onToggle);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [immersive, toggleFullscreen, exitFullscreen]);

  const playerClass = [
    'stage',
    'player',
    immersive ? 'player--immersive' : '',
    filling ? 'player--fill' : '',
    idle ? 'player--idle' : '',
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <section ref={playerRef} className={playerClass} onMouseMove={wakeChrome} onPointerDown={wakeChrome}>
      {/* Double-click on the picture, not the whole stage: a double-click on a button is not a request to
          change mode. */}
      <div className="player__video" onDoubleClick={toggleFullscreen}>
        <VideoStage />
      </div>

      {sessionState === 'error' ? (
        <p className="empty empty--error player__error">{sessionError ?? 'That day could not be opened.'}</p>
      ) : null}

      <div className="player__chrome" onMouseEnter={holdChrome} onMouseLeave={releaseChrome}>
        <TransportBar fullscreen={immersive} onToggleFullscreen={toggleFullscreen} />
        <Timeline />
        {immersive ? (
          <p className="player__hint">
            Esc leaves full screen · F toggles it · the controls hide when the mouse stops moving
          </p>
        ) : null}
      </div>
    </section>
  );
}

/** The reconstructed screen. Frames arrive already RGBA and are blitted as-is. */
function VideoStage() {
  const { bridge, frameSize, session, playing, gaps, positionUs } = useRecall();
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [drew, setDrew] = useState(false);

  useEffect(() => {
    const off = bridge.onFrame((frame) => {
      const canvas = canvasRef.current;
      const { meta, pixels } = frame;
      const width = Math.round(meta.width);
      const height = Math.round(meta.height);
      const expected = width * height * 4;
      if (!canvas || width <= 0 || height <= 0 || pixels.length < expected) {
        return;
      }

      if (canvas.width !== width) canvas.width = width;
      if (canvas.height !== height) canvas.height = height;

      const context = canvas.getContext('2d', { alpha: false });
      if (!context) {
        return;
      }

      // The buffer is released as soon as this handler returns, so the pixels are copied out here and now.
      // `createImageData` + `set` rather than `new ImageData(view, …)`: the incoming view is typed as
      // Uint8ClampedArray<ArrayBufferLike> and the ImageData constructor demands a real ArrayBuffer.
      const image = context.createImageData(width, height);
      image.data.set(pixels.subarray(0, image.data.length));
      context.putImageData(image, 0, 0);
      setDrew(true);
    });

    return off;
  }, [bridge]);

  // A frozen frame across a stretch nobody recorded is the one thing playback must not pretend. The held frame is
  // hidden rather than dimmed, and the card that replaces it is vector — DOM plus inline SVG — so it stays crisp at
  // any window size and can never be mistaken for something that was on screen.
  const gap = session ? gaps.find((item) => positionUs >= item.startUs && positionUs <= item.endUs) : undefined;

  return (
    <div className={gap ? 'screen screen--gap' : 'screen'} data-testid="screen">
      <canvas ref={canvasRef} className="screen__canvas" width={frameSize?.width ?? 16} height={frameSize?.height ?? 16} />
      {gap ? (
        <div className="screen__gap" role="status" aria-live="polite">
          <svg className="screen__gap-icon" viewBox="0 0 64 64" aria-hidden="true" focusable="false">
            <rect x="3" y="17" width="40" height="29" rx="5" />
            <path d="M43 28.5l15-9v25l-15-9z" />
            <circle cx="23" cy="31.5" r="8.5" />
            <line className="screen__gap-slash" x1="9" y1="56" x2="56" y2="8" />
          </svg>
          <p className="screen__gap-title">Recording unavailable</p>
          <p className="screen__gap-meta">{fmtDuration(gap.durationUs)} not recorded</p>
          <p className="screen__gap-note">
            {gap.reason} — the frame behind this card is the last one before it, so it stays hidden instead of standing
            in for what is on screen now.
          </p>
        </div>
      ) : null}
      {!drew ? (
        <div className="screen__placeholder">
          {session ? (
            <p>
              Opened {fmtDayLong(session.day)} — {fmtDuration(session.durationUs)} across {session.replays.length} recorded
              stretch{session.replays.length === 1 ? '' : 'es'}. Press play, or pick a stretch on the right.
            </p>
          ) : (
            <p>Select a recorded day on the left to reconstruct it.</p>
          )}
        </div>
      ) : null}
      <span className={`screen__badge ${playing ? 'screen__badge--live' : ''}`}>{playing ? 'playing' : 'paused'}</span>
    </div>
  );
}

/**
 * Play/pause, stepping, rate, quiet-skipping, frame export and the fullscreen toggle. The toggle is handed
 * down rather than read from context: the mode belongs to the stage, and the stage is the element it applies to.
 */
function TransportBar({ fullscreen, onToggleFullscreen }: { fullscreen: boolean; onToggleFullscreen: () => void }) {
  const {
    session,
    playing,
    speed,
    positionUs,
    firstUs,
    lastUs,
    durationUs,
    togglePlay,
    setSpeed,
    step,
    skipIdle,
    savePng,
  } = useRecall();

  useEffect(() => {
    const onToggle = () => togglePlay();
    const onStep = (event: Event) => {
      const detail = (event as CustomEvent<number>).detail;
      step(detail >= 0 ? 1 : -1);
    };

    window.addEventListener('screen-recall:toggle-play', onToggle);
    window.addEventListener('screen-recall:step', onStep);
    return () => {
      window.removeEventListener('screen-recall:toggle-play', onToggle);
      window.removeEventListener('screen-recall:step', onStep);
    };
  }, [togglePlay, step]);

  return (
    <div className="transport">
      <button type="button" className="btn btn--primary btn--wide" onClick={togglePlay} disabled={!session}>
        {playing ? '⏸ Pause' : '▶ Play'}
      </button>
      <button type="button" className="btn" onClick={() => step(-1)} disabled={!session} title="One event back (rebuilds from the nearest checkpoint)">
        ⏮ Step
      </button>
      <button type="button" className="btn" onClick={() => step(1)} disabled={!session} title="One event forward">
        Step ⏭
      </button>
      <button type="button" className="btn" onClick={skipIdle} disabled={!session} title="Jump past the next stretch with no change">
        Skip quiet
      </button>

      <label className="field field--inline">
        <span className="field__label">Speed</span>
        <select className="input" value={speed} disabled={!session} onChange={(event) => setSpeed(Number(event.target.value))}>
          {SPEED_OPTIONS.map((option) => (
            <option key={option} value={option}>
              {fmtSpeed(option)}
            </option>
          ))}
        </select>
      </label>

      <div className="transport__clock">
        <span className="clock" title="playhead">
          {session ? fmtClock(positionUs) : '--:--:--.---'}
        </span>
        <span className="clock clock--dim" title="recorded span of the day">
          {session ? `${fmtWallClock(firstUs)} – ${fmtWallClock(lastUs)}` : '—'}
        </span>
        <span className="clock clock--dim">{session ? fmtDurationClock(durationUs) : '—'}</span>
      </div>

      <button type="button" className="btn btn--ghost" onClick={savePng} disabled={!session}>
        Save PNG
      </button>

      <button
        type="button"
        className="btn btn--ghost"
        onClick={onToggleFullscreen}
        disabled={!session}
        aria-pressed={fullscreen}
        title={fullscreen ? 'Leave full screen (Esc)' : 'Full screen (F, or double-click the picture)'}
      >
        {fullscreen ? '⤡ Exit full screen' : '⛶ Full screen'}
      </button>
    </div>
  );
}

/**
 * The day's timeline. Quiet stretches are drawn as bands — the places a player holds one still frame — and
 * focus changes as ticks, so the shape of a day is visible before it is played.
 */
function Timeline() {
  const {
    session,
    firstUs,
    durationUs,
    positionUs,
    idles,
    gaps,
    spans,
    idleMinGapUs,
    setIdleMinGapUs,
    beginScrub,
    updateScrub,
    endScrub,
  } = useRecall();
  const [dragValue, setDragValue] = useState<number | null>(null);

  const span = durationUs > 0 ? durationUs : 1;
  const value = dragValue ?? progressOf(positionUs, firstUs, span) * 1000;

  return (
    <div className="timeline">
      <div className="timeline__marks" aria-hidden="true">
        {gaps.map((gap) => (
          <span
            key={`gap-${gap.startUs}`}
            className="timeline__gap"
            style={{
              left: `${progressOf(gap.startUs, firstUs, span) * 100}%`,
              width: `${Math.max(0.2, (gap.durationUs / span) * 100)}%`,
            }}
            title={`not recorded for ${fmtDuration(gap.durationUs)} — ${gap.reason}`}
          />
        ))}
        {idles.map((idle) => (
          <span
            key={`idle-${idle.startUs}`}
            className="timeline__idle"
            style={{
              left: `${progressOf(idle.startUs, firstUs, span) * 100}%`,
              width: `${Math.max(0.2, (idle.durationUs / span) * 100)}%`,
            }}
            title={`quiet for ${fmtDuration(idle.durationUs)}`}
          />
        ))}
        {spans.map((focus) => (
          <span
            key={`span-${focus.startUs}-${focus.appName}`}
            className="timeline__tick"
            style={{ left: `${progressOf(focus.startUs, firstUs, span) * 100}%` }}
          />
        ))}
        {session ? (
          <span className="timeline__playhead" style={{ left: `${progressOf(positionUs, firstUs, span) * 100}%` }} />
        ) : null}
      </div>

      <input
        className="timeline__range"
        type="range"
        min={0}
        max={1000}
        step={1}
        value={value}
        disabled={!session}
        aria-label="Playhead"
        onPointerDown={() => beginScrub(value / 1000)}
        onChange={(event) => {
          const next = Number(event.target.value);
          setDragValue(next);
          updateScrub(next / 1000);
        }}
        onPointerUp={() => {
          setDragValue(null);
          endScrub(value / 1000);
        }}
        onKeyUp={() => {
          setDragValue(null);
          endScrub(value / 1000);
        }}
      />

      <div className="timeline__foot">
        <span className="timeline__legend">
          <i className="swatch swatch--idle" /> quiet {idles.length}
          <i className="swatch swatch--gap" /> not recorded {gaps.length}
          <i className="swatch swatch--tick" /> focus {spans.length}
        </span>
        <label className="field field--inline">
          <span className="field__label">Quiet means at least</span>
          <select
            className="input"
            value={idleMinGapUs}
            disabled={!session}
            onChange={(event) => setIdleMinGapUs(Number(event.target.value))}
          >
            {IDLE_GAP_OPTIONS.map((option) => (
              <option key={option.us} value={option.us}>
                {option.label}
              </option>
            ))}
          </select>
        </label>
      </div>
    </div>
  );
}

/** Every recorded stretch of the selected day — what there is to replay, and what it cost. */
function ReplayList() {
  const { bridge, replays, replaysState, replayCheckpoints, session, durationUs } = useRecall();

  const totalEvents = replays.reduce((total, replay) => total + replay.entryCount, 0);
  const totalBytes = replays.reduce((total, replay) => total + replay.bytes, 0);

  return (
    <section className="block">
      <header className="block__head">
        <h3 className="block__title">Recorded stretches</h3>
        <span className="block__hint">{replayCheckpoints !== null ? `${replayCheckpoints} checkpoints` : ''}</span>
      </header>

      {!session ? (
        <p className="empty">Open a day to see what was recorded.</p>
      ) : replays.length === 0 ? (
        <p className="empty">{replaysState === 'loading' ? 'Reading the log…' : 'No recorded stretches for this day.'}</p>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Segment</th>
              <th scope="col">From</th>
              <th scope="col">To</th>
              <th scope="col">Length</th>
              <th scope="col">Events</th>
              <th scope="col">Size</th>
            </tr>
          </thead>
          <tbody>
            {replays.map((replay) => (
              <tr
                key={replay.fileName}
                className="row--clickable"
                tabIndex={0}
                title="Jump to the start of this stretch"
                onClick={() => bridge.seek(replay.startUs)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') bridge.seek(replay.startUs);
                }}
              >
                <th scope="row" className="mono">
                  {replay.fileName}
                </th>
                <td className="mono">{fmtWallClock(replay.startUs)}</td>
                <td className="mono">{fmtWallClock(replay.endUs)}</td>
                <td>{fmtDuration(replay.durationUs)}</td>
                <td>{fmtCount(replay.entryCount)}</td>
                <td>{fmtBytes(replay.bytes)}</td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr>
              <td colSpan={3}>
                {session ? `${session.checkpoints} checkpoints · ${session.width}×${session.height}` : ''}
              </td>
              <td>{fmtDuration(durationUs)}</td>
              <td>{fmtCount(totalEvents)}</td>
              <td>{fmtBytes(totalBytes)}</td>
            </tr>
          </tfoot>
        </table>
      )}
    </section>
  );
}

/** Jump-to-focus: the windows the recorder saw, kept as navigation metadata rather than analytics. */
function FocusList() {
  const { bridge, spans, spansState, session } = useRecall();

  return (
    <section className="block">
      <header className="block__head">
        <h3 className="block__title">Jump to focus</h3>
        <span className="block__hint">{spans.length > 0 ? `${spans.length}` : ''}</span>
      </header>

      {!session ? (
        <p className="empty">Focus spans come from the recorded day's index.</p>
      ) : spans.length === 0 ? (
        <p className="empty">{spansState === 'loading' ? 'Reading the index…' : 'No window spans recorded.'}</p>
      ) : (
        <ul className="focus">
          {spans.map((focus) => (
            <li key={`${focus.startUs}-${focus.appName}-${focus.windowTitle}`}>
              <button type="button" className="focus__item" onClick={() => bridge.seek(focus.startUs)}>
                <span className="focus__time">{fmtWallClock(focus.startUs)}</span>
                <span className="focus__app">{focus.appName}</span>
                <span className="focus__title">{focus.windowTitle}</span>
                <span className="focus__len">{fmtDuration(focus.endUs - focus.startUs)}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
