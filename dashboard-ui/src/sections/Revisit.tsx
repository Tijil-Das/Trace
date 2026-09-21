import { useEffect, useRef, useState } from 'react';

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
  const { days, daysState, daysError, selectedDay, openDay, sessionState, sessionError } = useRecall();

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

      <section className="stage">
        <VideoStage />

        {sessionState === 'error' ? (
          <p className="empty empty--error">{sessionError ?? 'That day could not be opened.'}</p>
        ) : null}

        <TransportBar />
        <Timeline />
      </section>

      <aside className="panel panel--side">
        <ReplayList />
        <FocusList />
      </aside>
    </div>
  );
}

/** The reconstructed screen. Frames arrive already RGBA and are blitted as-is. */
function VideoStage() {
  const { bridge, frameSize, session, playing } = useRecall();
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

  return (
    <div className="screen" data-testid="screen">
      <canvas ref={canvasRef} className="screen__canvas" width={frameSize?.width ?? 16} height={frameSize?.height ?? 16} />
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

/** Play/pause, stepping, rate, quiet-skipping and frame export. */
function TransportBar() {
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
