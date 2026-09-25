import { useEffect, useState } from 'react';

import { Revisit } from './sections/Revisit';
import { Settings } from './sections/Settings';
import { useRecall, type Section } from './state/RecallContext';

/**
 * Shell: a title bar, two sections (Revisit / Settings) and the toast rail.
 *
 * The two sections are the whole app. Revisit is where the day gets watched back; Settings is where the
 * recorder is configured and the service's live numbers are read.
 */
export function App() {
  const [section, setSection] = useState<Section>('revisit');
  const { mode, days, status, lastError, toasts, dismissToast, refresh } = useRecall();

  // Space, the arrows and F drive the player wherever focus is, except while typing in a field.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) {
        return;
      }

      if (event.code === 'Space') {
        event.preventDefault();
        window.dispatchEvent(new CustomEvent('screen-recall:toggle-play'));
      } else if (event.key === 'ArrowRight') {
        event.preventDefault();
        window.dispatchEvent(new CustomEvent('screen-recall:step', { detail: 1 }));
      } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        window.dispatchEvent(new CustomEvent('screen-recall:step', { detail: -1 }));
      } else if ((event.key === 'f' || event.key === 'F') && !event.ctrlKey && !event.altKey && !event.metaKey) {
        // "f" is what every video player uses for fullscreen; Ctrl+F stays the browser's own find.
        event.preventDefault();
        window.dispatchEvent(new CustomEvent('screen-recall:toggle-fullscreen'));
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  return (
    <div className="app">
      <header className="app__bar">
        <div className="app__brand">
          <span className="app__mark" aria-hidden="true" />
          <div>
            <h1 className="app__title">Screen Recall</h1>
            <p className="app__subtitle">
              {days.length > 0 ? `${days.length} recorded day${days.length === 1 ? '' : 's'}` : 'no recorded days'}
              {status?.day ? ` · recording ${status.day}` : ''}
            </p>
          </div>
        </div>

        <nav className="app__tabs" role="tablist" aria-label="Sections">
          <button
            type="button"
            role="tab"
            aria-selected={section === 'revisit'}
            className={`tab ${section === 'revisit' ? 'tab--active' : ''}`}
            onClick={() => setSection('revisit')}
          >
            Revisit
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={section === 'settings'}
            className={`tab ${section === 'settings' ? 'tab--active' : ''}`}
            onClick={() => setSection('settings')}
          >
            Settings
          </button>
        </nav>

        <div className="app__meta">
          <span
            className={`pill ${status?.paused ? 'pill--warn' : status ? 'pill--ok' : 'pill--muted'}`}
            title={status ? `service ${status.state ?? 'unknown'}` : 'the capture service did not answer'}
          >
            {status?.paused ? 'paused' : status ? 'recording' : 'service offline'}
          </span>
          <span className={`pill ${mode === 'mock' ? 'pill--warn' : 'pill--muted'}`} title="how this UI is talking to the recorder">
            {mode === 'mock' ? 'dev mock' : 'connected'}
          </span>
          <button type="button" className="btn btn--ghost" onClick={refresh}>
            Refresh
          </button>
        </div>
      </header>

      {lastError ? (
        <div className="banner banner--error" role="alert">
          <strong>Host error:</strong> {lastError}
        </div>
      ) : null}

      <main className="app__body">
        {section === 'revisit' ? <Revisit /> : <Settings />}
      </main>

      {toasts.length > 0 ? (
        <div className="toasts" role="status" aria-live="polite">
          {toasts.map((toast) => (
            <div key={toast.id} className={`toast toast--${toast.kind}`}>
              <div>
                <p className="toast__message">{toast.message}</p>
                {toast.detail ? <p className="toast__detail">{toast.detail}</p> : null}
              </div>
              <button type="button" className="toast__close" onClick={() => dismissToast(toast.id)} aria-label="Dismiss">
                ×
              </button>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
}
