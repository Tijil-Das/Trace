import { useEffect, useState } from 'react';

import type { FidelityMode, RecallConfig } from '../bridge/protocol';
import { fmtBytes, fmtCount, fmtDuration, fmtNumber, fmtPercent, fmtText } from '../lib/format';
import { useRecall } from '../state/RecallContext';

/**
 * Reads a numeric field defensively.
 *
 * `RecallConfig` and `CaptureStatus` carry an index signature, so any field the protocol does not name
 * explicitly arrives as `unknown`. Reading it through here is what keeps that honest instead of casting.
 */
function num(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

/**
 * Settings: what the recorder is doing right now, what it is allowed to record, and the destructive buttons.
 *
 * Edits stay local until Save, so a half-typed retention number is never pushed at a running service.
 */
export function Settings() {
  const {
    status,
    statusState,
    statusAt,
    config,
    configState,
    configAt,
    reloadConfig,
    saveConfig,
    pauseCapture,
    resumeCapture,
    purgeRecent,
    prune,
    openStorage,
    recorder,
    requestRecorder,
    startRecorder,
    stopRecorder,
    setStartWithWindows,
  } = useRecall();

  const [draft, setDraft] = useState<RecallConfig | null>(null);
  const [purgeMinutes, setPurgeMinutes] = useState(15);

  // Adopt the host's config whenever it arrives, but never overwrite edits the user has not saved yet.
  useEffect(() => {
    if (config) {
      setDraft((prev) => (prev === null ? { ...config } : prev));
    }
  }, [config]);

  const dirty = Boolean(draft && config && JSON.stringify(draft) !== JSON.stringify(config));

  const set = <K extends keyof RecallConfig>(key: K, value: RecallConfig[K]) => {
    setDraft((prev) => (prev ? { ...prev, [key]: value } : prev));
  };

  const listToText = (list: string[] | undefined): string => (list ?? []).join('\n');
  const textToList = (text: string): string[] =>
    text
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.length > 0);

  return (
    <div className="settings">
      <section className="panel">
        <header className="panel__head">
          <h2 className="panel__title">Recorder process</h2>
          <span className={`pill ${recorder?.running ? 'pill--ok' : 'pill--muted'}`}>
            {recorder ? (recorder.running ? 'running' : 'stopped') : 'unknown'}
          </span>
        </header>

        <p className="panel__note">
          Recording runs in its own process, so it keeps going when this window is closed to the tray. Pausing stops
          new entries; stopping ends the process.
        </p>

        <label className="check">
          <input
            type="checkbox"
            checked={recorder?.startWithWindows ?? false}
            disabled={!recorder}
            onChange={(event) => setStartWithWindows(event.target.checked)}
          />
          <span>Start the recorder when Windows starts</span>
        </label>
        <p className="field__hint">
          A per-user sign-in entry, so only the recorder starts with Windows — never this dashboard.
        </p>

        {recorder?.executable ? <p className="field__hint mono">{recorder.executable}</p> : null}
        {recorder?.startupCommand ? <p className="field__hint mono">{recorder.startupCommand}</p> : null}

        <div className="row row--wrap">
          <button type="button" className="btn btn--primary" onClick={startRecorder} disabled={recorder?.running === true}>
            Start recorder
          </button>
          <button
            type="button"
            className="btn btn--danger"
            disabled={recorder?.running !== true}
            onClick={() => {
              if (window.confirm('Stop the recorder? It will flush what it has, but anything since its last flush can be lost.')) {
                stopRecorder();
              }
            }}
          >
            Stop recorder
          </button>
          <button type="button" className="btn btn--ghost" onClick={requestRecorder}>
            Refresh
          </button>
        </div>
      </section>

      <section className="panel">
        <header className="panel__head">
          <h2 className="panel__title">Capture service</h2>
          <span className="panel__hint">
            {statusState === 'loading' ? 'asking…' : statusAt ? `updated ${new Date(statusAt).toLocaleTimeString()}` : 'no answer'}
          </span>
        </header>

        {statusState === 'empty' || !status ? (
          <p className="empty">
            The capture service is not running, so there is no live status. Recording happens in a separate process — the
            dashboard only reads it and sends it commands.
          </p>
        ) : (
          <>
            <dl className="stats">
              <Stat label="State" value={fmtText(status.state)} />
              <Stat label="Paused" value={status.paused ? 'yes' : 'no'} />
              <Stat label="Uptime" value={fmtDuration((status.uptimeSeconds ?? status.uptime ?? 0) * 1_000_000)} />
              <Stat label="CPU" value={fmtPercent(status.cpuPercent)} />
              <Stat label="Working set" value={`${fmtNumber(status.workingSetMb)} MB`} />
              <Stat label="Frames acquired" value={fmtCount(status.framesAcquired)} />
              <Stat label="Frames with changes" value={fmtCount(status.framesWithChanges)} />
              <Stat label="Tiles hashed" value={fmtCount(status.tilesHashed)} />
              <Stat label="Tiles stored" value={fmtCount(status.tilesStored)} />
              <Stat label="Tiles deduped" value={fmtCount(status.tilesDeduped)} />
              <Stat label="Log entries" value={fmtCount(status.logEntries)} />
              <Stat label="Assets on disk" value={fmtCount(status.assetCountOnDisk)} />
              <Stat label="Assets written" value={fmtBytes(status.assetBytesWritten)} />
              <Stat label="Store size" value={fmtBytes(num(status.assetBytesOnDisk))} />
              <Stat label="Session bytes" value={fmtBytes(status.sessionBytes)} />
              <Stat label="Free disk" value={`${fmtNumber(status.freeDiskGb)} GB`} />
              <Stat label="Grid tiles" value={fmtCount(num(status.canvasTiles))} />
              <Stat label="Write queue" value={fmtCount(status.assetQueueDepth ?? status.queueDepth)} />
              <Stat label="Monitors" value={fmtCount(status.monitors)} />
              <Stat label="Foreground" value={fmtText(status.foregroundApp)} />
              <Stat label="Storage root" value={fmtText(status.storageRoot)} wide />
              <Stat label="Recording day" value={fmtText(status.day)} />
              <Stat label="Last maintenance" value={fmtText(status.lastMaintenance)} wide />
            </dl>

            {status.lastError ? <p className="empty empty--error">Last error: {status.lastError}</p> : null}

            <div className="row row--wrap">
              <button type="button" className="btn" onClick={pauseCapture}>
                Pause capture
              </button>
              <button type="button" className="btn" onClick={resumeCapture}>
                Resume capture
              </button>
              <button type="button" className="btn btn--ghost" onClick={openStorage}>
                Open storage folder
              </button>
            </div>
          </>
        )}
      </section>

      <section className="panel">
        <header className="panel__head">
          <h2 className="panel__title">Recording settings</h2>
          <span className="panel__hint">{configAt ? `loaded ${new Date(configAt).toLocaleTimeString()}` : ''}</span>
        </header>

        {!draft ? (
          <p className="empty">
            {configState === 'loading' ? 'Reading settings…' : 'No settings available — the service did not answer.'}
          </p>
        ) : (
          <div className="form">
            <label className="field">
              <span className="field__label">Storage folder</span>
              <input className="input" type="text" value={draft.storagePath ?? ''} onChange={(e) => set('storagePath', e.target.value)} />
              <span className="field__hint">Where recordings live. Changing it starts a new store at the new path.</span>
            </label>

            <div className="grid2">
              <label className="field">
                <span className="field__label">Keep recordings for (days)</span>
                <input className="input" type="number" min={1} max={3650} value={draft.retentionDays ?? 30} onChange={(e) => set('retentionDays', Number(e.target.value))} />
              </label>
              <label className="field">
                <span className="field__label">Checkpoint every (seconds)</span>
                <input className="input" type="number" min={10} max={3600} value={draft.checkpointSeconds ?? 180} onChange={(e) => set('checkpointSeconds', Number(e.target.value))} />
              </label>
              <label className="field">
                <span className="field__label">Tile size (px)</span>
                <input className="input" type="number" min={16} max={512} value={draft.tileSize ?? 64} onChange={(e) => set('tileSize', Number(e.target.value))} />
                <span className="field__hint">Must stay constant for a store, or tile dedupe stops matching.</span>
              </label>
              <label className="field">
                <span className="field__label">Fidelity</span>
                <select className="input" value={draft.fidelityMode ?? 'lossless'} onChange={(e) => set('fidelityMode', e.target.value as FidelityMode)}>
                  <option value="lossless">lossless — QOI (default, fastest)</option>
                  <option value="archive">archive — QOI + Deflate (lossless, ~22% smaller)</option>
                  <option value="balanced">balanced — 5-6-5 colour (lossy, smallest)</option>
                </select>
                <span className="field__hint">
                  Lossless is bit-exact either way; archive trades ~3× the encode CPU per changed tile for ~22% less
                  storage. Balanced discards colour precision — lossless is what the fidelity harness verifies.
                </span>
              </label>
              <label className="field">
                <span className="field__label">Daily budget (MB)</span>
                <input className="input" type="number" min={64} value={num(draft.maxDailyMegabytes) ?? 2048} onChange={(e) => set('maxDailyMegabytes', Number(e.target.value))} />
              </label>
              <label className="field">
                <span className="field__label">Stop below free disk (MB)</span>
                <input className="input" type="number" min={128} value={num(draft.minFreeDiskMegabytes) ?? 2048} onChange={(e) => set('minFreeDiskMegabytes', Number(e.target.value))} />
              </label>
            </div>

            <label className="check">
              <input type="checkbox" checked={Boolean(draft.captureAllMonitors)} onChange={(e) => set('captureAllMonitors', e.target.checked)} />
              <span>Capture every monitor</span>
            </label>
            <label className="check">
              <input type="checkbox" checked={Boolean(draft.pauseOnBattery)} onChange={(e) => set('pauseOnBattery', e.target.checked)} />
              <span>Pause while on battery</span>
            </label>
            <label className="check check--disabled">
              <input type="checkbox" checked={Boolean(draft.encryptionAtRest)} disabled />
              <span>
                Encrypt at rest <em>(not implemented yet — the switch exists, the envelope does not)</em>
              </span>
            </label>

            <label className="field">
              <span className="field__label">Excluded processes (one per line)</span>
              <textarea className="input input--area" rows={4} value={listToText(draft.excludedProcesses)} onChange={(e) => set('excludedProcesses', textToList(e.target.value))} />
            </label>

            <label className="field">
              <span className="field__label">Excluded window titles (one fragment per line)</span>
              <textarea className="input input--area" rows={4} value={listToText(draft.excludedTitlePatterns)} onChange={(e) => set('excludedTitlePatterns', textToList(e.target.value))} />
            </label>

            <div className="row row--wrap">
              <button type="button" className="btn btn--primary" disabled={!dirty} onClick={() => saveConfig(draft)}>
                {dirty ? 'Save settings' : 'Saved'}
              </button>
              <button type="button" className="btn btn--ghost" onClick={() => setDraft(config ? { ...config } : null)} disabled={!dirty}>
                Discard changes
              </button>
              <button type="button" className="btn btn--ghost" onClick={reloadConfig}>
                Reload from service
              </button>
            </div>
          </div>
        )}
      </section>

      <section className="panel panel--danger">
        <header className="panel__head">
          <h2 className="panel__title">Delete recordings</h2>
        </header>
        <p className="panel__note">These are immediate and cannot be undone. Tiles no surviving day references go too.</p>
        <div className="row row--wrap">
          <label className="field field--inline">
            <span className="field__label">Last</span>
            <input className="input input--small" type="number" min={1} max={43200} value={purgeMinutes} onChange={(e) => setPurgeMinutes(Number(e.target.value))} />
            <span className="field__label">minutes</span>
          </label>
          <button
            type="button"
            className="btn btn--danger"
            onClick={() => {
              if (window.confirm(`Delete the last ${purgeMinutes} minute(s) of recording and reclaim their tiles?`)) {
                purgeRecent(purgeMinutes);
              }
            }}
          >
            Delete recent
          </button>
          <button
            type="button"
            className="btn btn--danger"
            onClick={() => {
              if (window.confirm('Run retention pruning now and delete everything past the retention window?')) {
                prune();
              }
            }}
          >
            Prune now
          </button>
        </div>
      </section>
    </div>
  );
}

function Stat({ label, value, wide }: { label: string; value: string; wide?: boolean }) {
  return (
    <div className={`stat ${wide ? 'stat--wide' : ''}`} title={`${label}: ${value}`}>
      <dt className="stat__label" title={label}>{label}</dt>
      <dd className="stat__value" title={value}>{value}</dd>
    </div>
  );
}
