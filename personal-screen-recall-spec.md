# Personal Screen Recall — Engineering Specification

**Status:** Design spec, ready for implementation
**Target platform:** Windows 10/11, x64
**Audience:** A developer (human or AI coding agent) implementing this from scratch

\---

## 1\. Product summary

A locally-installed Windows application that continuously and losslessly records what is visibly drawn on the user's screen, storing it not as video but as a content-addressable tile cache + timestamped reference log — the same principle RDP uses to send a usable desktop over kilobits. Playback reconstructs exact frames on demand through a custom player. A background service does the capturing; a separate dashboard app is where the user reviews, configures, and manages recordings.

\---

## 2\. Goals

* Near-lossless, scrubbable reconstruction of everything that was visibly on screen, indexed by time.
* Steady-state CPU usage under 2%. GPU usage is allowed to be moderate — this is a deliberate trade, not a compromise.
* Realistic daily storage footprint under \~2 GB/day for typical mixed use, without ever encoding full-motion video as the primary format.
* Configurable storage location and configurable retention window (e.g. 30 / 120 / 150 days), with automatic pruning.
* A single installable package: background capture service + dashboard app + installer.

### 2.1 Explicit non-goals

State these clearly to whoever builds this, since it's easy to drift toward the wrong product:

* This is **not a productivity/usage-analytics tool.** No "time spent per app" scoring, no automated categorization, no behavioral dashboards. Any per-window timing data that exists is there purely to let the player jump to "the next time Chrome was focused" — navigation metadata, not analytics.
* This is **not a periodic-screenshot tool.** Capture is event-driven off actual screen changes, not a timer.
* This is **not a video encoder.** No H.264/265 compression loop runs in the steady-state capture path. Video export is an optional, occasional output format — never the native storage format.

\---

## 3\. Hard requirements

|Requirement|Target|
|-|-|
|Steady-state CPU|< 2% average|
|GPU|Light–moderate use acceptable (roughly 5–20%)|
|Reconstruction fidelity|\~100% (lossless by default) for anything drawn through the desktop compositor|
|Daily storage|Up to \~2 GB/day acceptable; typical mixed-use days should land well under that|
|Retention|User-configurable in days (e.g. 30, 120, 150+), auto-pruned|
|Storage location|User-configurable path, changeable after install|
|Delivery|Installable Windows app: background service + dashboard, single installer|

\---

## 4\. High-level architecture

```text
 Windows Desktop (DWM-composited surface, all windows/monitors)
              │
              │  IDXGIOutputDuplication (DXGI Desktop Duplication API)
              ▼
 ┌─────────────────────────────────────────────────────┐
 │ Capture Service — "ScreenRecall.Capture"            │
 │ Windows Service, runs at Below Normal priority      │
 │                                                     │
 │  1. Frame acquire loop (adaptive cadence)           │
 │  2. Dirty-rect / move-rect extraction               │
 │  3. Grid-aligned tiling of changed regions          │
 │  4. Content hash per tile (GPU-assisted)            │
 │  5. Content-addressable dedupe lookup               │
 │  6. Lossless/near-lossless tile compression         │
 │  7. Reference log append                            │
 │  8. Periodic checkpoint write                       │
 │  9. Exclusion-list filter (skip denylisted windows) │
 └───────────────────────┬─────────────────────────────┘
                         │writes
                         ▼
 ┌────────────────────────────────────────────────────────┐
 │ Local Store — user-chosen path                         │
 │  /assets/           content-addressable tile cache     │
 │  /sessions/YYYY-MM-DD/log.bin       reference log      │
 │  /sessions/YYYY-MM-DD/checkpoints/  periodic snapshots │
 │  /index.db          SQLite: day/window boundaries only │
 │  (retention/pruning job runs against this tree)        │
 └───────────────────────┬────────────────────────────────┘
                         │reads
                         ▼
 ┌─────────────────────────────────────────────────────┐
 │ Dashboard App — tray-resident desktop app           │
 │  Timeline / calendar view · Player · Settings       │
 │  Live resource monitor · Pause/panic-purge controls │
 └─────────────────────────────────────────────────────┘
```

Two processes, connected by local IPC (named pipe). The service never depends on the dashboard being open; the dashboard never touches capture files directly — it always talks to the service, avoiding file-lock/corruption issues.

\---

## 5\. Component 1 — Capture service

### 5.1 Capture loop

Use `IDXGIOutputDuplication::AcquireNextFrame` with an adaptive timeout. On success, read `DXGI\_OUTDUPL\_FRAME\_INFO`, then call `GetFrameDirtyRects` and `GetFrameMoveRects` to get exactly which screen regions changed — never process a full frame when only a corner of it changed.

**Adaptive cadence:**

* **Idle / static screen:** poll interval backs off (e.g. up to 1–2s between checks) — nothing to do if nothing changed.
* **Active use (typing, reading, scrolling text):** effectively event-driven off dirty rects, no fixed frame rate needed.
* **Bursty visual change (fast scroll, animation, video playback):** cadence tightens automatically since dirty-rect events arrive back-to-back; this is where the brief CPU/GPU spikes come from.

### 5.2 Tiling and hashing

Split each dirty rect into a fixed, screen-position-aligned grid (e.g. 64×64 tiles, aligned to absolute screen coordinates, not relative to the dirty rect). Alignment matters: it's what makes an icon that appears in the same screen position on two different days hash identically and dedupe, instead of missing by a few pixels of offset.

Hash each tile's content with a fast, non-cryptographic hash (xxHash3 or BLAKE3) — this can run on the GPU via a compute shader for the hashing/diffing step, which is the intended use of the "GPU can be used a bit more" budget, keeping CPU nearly idle.

### 5.3 Content-addressable dedupe

Before storing a tile, check whether its hash already exists in the asset store. If yes, write only a reference (8 bytes) to the log. If no, compress and store the tile once, then write the reference. This is the single biggest lever on file size — most UI (icons, backgrounds, repeated chrome, static text) reappears constantly across a session.

### 5.4 Compression

Default to a lossless codec for stored tiles — WebP lossless, PNG, or QOI (very fast to encode, well suited to flat UI content). Offer a "balanced" mode using near-lossless/lossy WebP for users who want smaller files and can tolerate minor artifacting; lossless stays the default given the fidelity requirement.

### 5.5 Reference log format

Append-only binary log, one file per day, e.g. `/sessions/2026-09-20/log.bin`:

```c
struct LogEntry {
    uint64\_t timestamp\_us;   // capture timestamp
    uint32\_t window\_id;      // stable per-window session ID
    uint16\_t monitor\_id;
    uint16\_t tile\_x, tile\_y; // grid coordinates
    uint64\_t asset\_hash;     // reference into the content-addressable store
    uint8\_t  op;             // DRAW | MOVE | CLEAR
};
```

Sequential, append-only, cheap to write, cheap to stream-replay.

### 5.6 Checkpointing

Every few minutes, write a full "visible state" snapshot — the complete map of every on-screen tile position to its current asset hash. This is the equivalent of a video keyframe: it lets playback seek to any timestamp by loading the nearest prior checkpoint and replaying forward only from there, instead of replaying an entire session from the start.

### 5.7 Exclusion list and pause control

A denylist of process names / window titles the service silently skips (recommended sane defaults: password managers, browser private/incognito windows where detectable). A global hotkey and a persistent tray icon toggle recording on/off — always visibly indicate recording state; this is good practice even for a single-user personal tool.

### 5.8 Resource governance

Run the service at `PROCESS\_MODE\_BACKGROUND\_BEGIN` / Below Normal priority and Low I/O priority so it never contends with foreground work. Self-throttle: if recent frame-processing time trends up, widen the capture interval automatically rather than let CPU climb.

\---

## 6\. Component 2 — Storage subsystem

**Asset store:** content-addressable, sharded by hash prefix (same pattern as git's object store, e.g. `/assets/ab/cd/abcdef123...webp`) to avoid one directory holding millions of files.

**Metadata index** (SQLite, WAL mode) — for navigation only, not analytics:

```sql
CREATE TABLE sessions (
  day TEXT PRIMARY KEY,
  start\_ts INTEGER,
  end\_ts INTEGER,
  log\_path TEXT
);

CREATE TABLE window\_spans (
  id INTEGER PRIMARY KEY,
  day TEXT,
  app\_name TEXT,
  window\_title TEXT,
  start\_ts INTEGER,
  end\_ts INTEGER
);

CREATE TABLE checkpoints (
  day TEXT,
  ts INTEGER,
  path TEXT
);
```

This index only stores boundaries — when a given window had focus — so the player can jump around. It is not a usage-analytics layer, the same way a video file's keyframe index isn't "analytics."

**Retention and pruning:** a background job deletes session directories older than the configured retention window, then garbage-collects any asset in `/assets/` no longer referenced by any log within the retention window.

**Storage location:** kept in a small config file (e.g. `%ProgramData%\\ScreenRecall\\config.json`). Changing it in the dashboard triggers a background migration (copy to new path, verify, then repoint, then delete old) rather than an in-place move, so a failure mid-migration can't lose data.

**Encryption at rest (recommended default-on):** AES-256-GCM over the asset store and logs, with the key wrapped via Windows DPAPI (bound to the logged-in user) or an optional user passphrase. Given this data can contain anything that was ever visibly on screen — passwords in unmasked fields, private messages, financial detail — encrypting at rest by default is a reasonable default, not an afterthought.

\---

## 7\. Component 3 — Reconstruction / player

**Seek:** locate the nearest checkpoint at or before the target timestamp, load its tile map into an in-memory canvas, then replay log entries between the checkpoint and the target, applying each DRAW/MOVE/CLEAR op. With checkpoints every few minutes, this makes arbitrary seeking near-instant.

**Playback:** continuous forward replay of the reference log against the canvas. This is cheaper than video decode — it's tile blits, not decompression of a bitstream — so real-time or faster-than-real-time scrubbing is easy on any modern GPU.

**Export (occasional, not native):** optionally render a selected time range out to MP4 via a hardware encoder (Media Foundation / NVENC / Quick Sync) for sharing a clip. This is a one-off output path, not how data is stored day to day.

**Optional text-index module (off by default):** a parallel, togglable pass that captures window text via UI Automation and feeds a separate full-text search table (timestamp, window, text). Kept fully separate from the pixel path so it's additive — search convenience — never the primary record.

\---

## 8\. Component 4 — Dashboard

* Calendar/timeline strip of recorded days.
* Scrub bar with play / pause / variable speed.
* "Jump to next/previous focus" list per window (driven by `window\_spans`, purely for navigation).
* Settings: storage path, retention days, exclusion list, monitor selection, fidelity mode (lossless vs balanced), encryption toggle.
* Live resource meter showing the capture service's own current CPU/GPU/disk usage — transparency into what it's actually costing you.
* Pause/resume toggle and a "purge last 15 minutes" panic button.
* Search bar, only shown if the text-index module is enabled.

\---

## 9\. Component 5 — Background service and IPC

Registered as a Windows Service (e.g. `ScreenRecallCapture`), auto-start, with SCM recovery options set to restart on failure. Dashboard and service communicate over a local named pipe (or local gRPC if you want typed contracts) — the dashboard sends config changes and pause/resume commands, and reads live stats; it never opens the log/asset files directly while the service is writing them.

\---

## 10\. Installer

Use WiX Toolset (MSI) or MSIX packaging. Authenticode-sign the binaries to avoid SmartScreen friction — note this project deliberately avoids any kernel-mode driver (no WHQL/driver-signing pipeline needed), which is what keeps it buildable as a single-developer or AI-agent project rather than a multi-week driver certification effort.

**First-run wizard:** choose storage folder, set retention days, select monitors, configure exclusion list, enable/disable encryption. Uninstaller should explicitly prompt whether to keep or securely delete all recorded data.

\---

## 11\. Recommended tech stack

|Component|Recommendation|
|-|-|
|Capture service|Rust (`windows-rs`) or C++ for the tight capture loop; C# with .NET AOT is acceptable since the budget is dominated by hashing/IO, not language overhead|
|Dashboard|WinUI 3 or WPF — native, to avoid Electron's own overhead undermining the lightweight requirement|
|Storage|SQLite (metadata/index only) + flat binary log + content-addressable file store|
|IPC|Named pipes (simplest) or local gRPC if typed contracts are preferred|
|Installer|WiX (MSI) or MSIX|

\---

## 12\. Performance budget and validation plan

* Run under typical mixed use for a full 8+ hour session; sample CPU/GPU/disk via Process Explorer or ETW counters every 30s; assert P95 CPU < 2%, GPU within the 5–20% band.
* Build a fidelity test harness: in a test-only mode, capture a "ground truth" full frame in parallel with normal operation, then pixel-diff it against the frame reconstructed by the player at the same timestamp. Target > 99.9% pixel match for anything drawn through the compositor.

\---

## 13\. Known limitations

* **DRM-protected video** (streaming services, etc.) is blocked from capture by design (HDCP) — will render black in both the live capture and the reconstruction. Not a bug, not fixable at this layer.
* **Exclusive-fullscreen apps** (some older games) can bypass the compositor; capture may drop in fidelity or go blank for that window. Worth flagging in the exclusion list UI rather than fighting.
* **Secure Desktop surfaces** (UAC prompts, the lock screen, Ctrl+Alt+Del screen) are intentionally uncapturable by any user-mode app — Windows blocks this at the OS level, by design, for security. This should not be worked around.
* **Nested Remote Desktop sessions** may need special-casing to avoid double-capture.

\---

## 14\. Security and privacy notes

This system will, by design, capture anything visibly on screen — including passwords typed into unmasked fields, private messages, and financial information. Recommended defaults:

* Ship with a sane exclusion-list default (password managers, detectable private/incognito browser windows).
* Encrypt at rest by default, not as an opt-in.
* Keep a persistent, unmissable recording indicator (tray icon) — never let it run silently and invisibly, even for a single-user personal tool.
* No network or cloud sync by default. Fully local unless explicitly added later.

\---

## 15\. Suggested repo layout

```text
screen-recall/
  capture-service/     # Windows Service: DXGI loop, tiling, hashing, log writer
  storage-lib/          # shared: asset store, log format, SQLite index schema
  player-lib/            # reconstruction/rendering engine (checkpoint + replay)
  dashboard-app/          # WinUI3/WPF app, tray icon, settings, player UI
  installer/               # WiX/MSIX project, first-run wizard
  docs/
```

\---

## 16\. Suggested build order

1. Capture service MVP: DXGI loop → dirty rects → raw log, no dedupe yet. Validate CPU/GPU numbers first, before optimizing size.
2. Add tiling, hashing, content-addressable dedupe. Validate storage-size numbers.
3. Add checkpointing; build a minimal CLI player. Validate seek speed and pixel fidelity.
4. Build the dashboard shell; wire it to the service over IPC.
5. Add settings (storage path, retention, exclusions) and the pruning job.
6. Add the installer, first-run wizard, code signing.
7. Optional: text-index module, MP4 export, encryption at rest (or pull this earlier if it's a priority).

