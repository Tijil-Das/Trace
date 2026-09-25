# Screen Recall dashboard UI

React + TypeScript + Vite front-end for the Screen Recall dashboard. It is normally hosted by the WPF shell in
`../dashboard-app` inside a WebView2 control; it also runs standalone in a browser against a built-in mock.

## Commands

```powershell
# npm.ps1 is blocked by execution policy on this machine, so always go through cmd:
cmd /c npm install
cmd /c npm run dev       # dev server on http://localhost:5173, against the dev mock
cmd /c npm run build     # tsc --noEmit && vite build -> dist/
cmd /c npm run typecheck
```

`npm run build` must exit 0 and produce `dist/index.html`. `tsconfig.json` is strict (`noUnusedLocals`,
`noUnusedParameters`, `noImplicitReturns`, `verbatimModuleSyntax`), so a type error fails the build rather than
being stripped.

## How the host finds it

`WebDashboardBridge.FindUiFolder()` walks up from the dashboard executable looking for
`dashboard-ui/dist/index.html`, and serves that folder from the WebView2 virtual host
`https://screenrecall.local`. If `dist/` is missing — or the WebView2 runtime is not installed — the host keeps
its original WPF panels instead, so a front-end build is never required to run the recorder.

To iterate against the real engine without rebuilding the front-end each time:

```powershell
$env:SCREENRECALL_UI_DEV = 'http://localhost:5173'   # then start the dashboard
cmd /c npm run dev
```

## Sections

- **Revisit** — pick a recorded day, watch it back, inspect what was recorded.
  - Frame stage: a `<canvas>` painted straight from the host's shared buffer.
  - Transport: play/pause, step by one event, skip the next quiet stretch, 0.5×–8×, Save PNG, full screen, playhead clock.
  - Timeline: quiet stretches drawn as bands, focus changes as ticks, playhead, scrubbing.
  - **Recorded stretches**: one row per log segment — the day's replay list — with a jump-to-start action.
  - Jump to focus: window spans from the navigation index.
- **Settings** — live capture-service status (polled every 2 s) and the recording configuration, plus
  pause/resume, purge-recent, prune-now and open-storage-folder.

Space toggles playback, ←/→ step and `F` toggles full screen, except while a text field has focus.

## Full screen

The player goes full screen the way a video player does — the picture fills the display, the transport and the
timeline float over it, and they fade out after ~2.5 s of no mouse, click or key and come back the moment there
is one. They stay put while the pointer rests on them, the cursor goes with them, and `Esc` — or a second click
on the button, or `F`, or a double-click on the picture — leaves the mode.

The mode is the browser's own (`requestFullscreen` on the stage element), not a CSS impression of one, which is
what lets the WPF shell follow: WebView2 raises `ContainsFullScreenElementChanged`, `WebDashboardBridge` reports
it through `FullScreenChanged`, and `MainWindow` puts the window exactly over the monitor — borderless, at the
monitor's own rectangle (taskbar included), with the window's padding dropped for as long as the React UI is in
charge — so the picture reaches the edges of the display and lands on whole pixels. Nothing about playback
changes with it — the page owns the mode and the window only stops being in the way. If the API is unavailable or
the host refuses the request, the same layout is applied as an in-window overlay instead, so the mode is never a
dead button.

## Frames

Frames arrive out of band on the WebView2 `sharedbufferreceived` event as a shared-memory buffer:

```js
window.chrome.webview.addEventListener('sharedbufferreceived', (event) => {
  const meta = event.additionalData;        // { type:'frame', width, height, positionUs, … }
  const buffer = event.getBuffer();         // RGBA, meta.width * meta.height * 4 bytes
  // draw synchronously, then the transport releases the buffer
});
```

The pixels are already RGBA with opaque alpha (the host swaps bytes and forces alpha while copying). **The host
only sends a frame when the picture actually changed**: during a quiet stretch of a day nothing arrives, and
the canvas simply keeps showing the last frame. That is the whole point of the log-based reconstruction — a
still minute costs nothing.

## The dev mock

`src/bridge/transport.ts` picks the WebView2 transport when `window.chrome.webview` exists and the mock
(`src/bridge/mockHost.ts`) otherwise. The mock answers every command, draws a moving synthetic picture keyed off
the playhead, and reports fake status/config, so the UI is fully explorable from `npm run dev`.

## Protocol

`src/bridge/protocol.ts` is the single source of truth for the wire format; the C# side implements exactly the
same commands and message types in `../dashboard-app/Services/WebDashboardBridge*.cs`. Keep the two in step.

Timestamps are **microseconds since the Unix epoch, local wall clock**; durations are microseconds; days are
local calendar days formatted `YYYY-MM-DD`. Every incoming field is optional and read defensively — the UI
renders `—` rather than guessing.
