# Architecture

Two processes, one store, no video anywhere.

```text
   Windows desktop (DWM-composited, all monitors)
              │
              │  IDXGIOutputDuplication  (dirty rects + move rects)
              ▼
   ┌──────────────────────────────────────────────────────────────┐
   │ ScreenRecall.CaptureService                                  │
   │                                                              │
   │  capture thread (Below Normal, background mode)               │
   │    acquire frame → grid cells → per-tile xxHash3              │
   │    → compare with in-memory canvas → log only what changed    │
   │    → encode (QOI) → hand payload to the writer thread         │
   │                                                              │
   │  writer thread (Below Normal)                                 │
   │    write new tiles into the content-addressable store         │
   │                                                              │
   │  named pipe ScreenRecall.Capture → status/config/pause/purge  │
   └──────────────────────────────────────────────────────────────┘
              │ writes
              ▼
   store root (user-chosen)
     assets/ab/cd/<hash>.tile          content-addressable, sharded
     sessions/YYYY-MM-DD/log.bin       27-byte fixed records
     sessions/YYYY-MM-DD/checkpoints/  full-state snapshots
     sessions/YYYY-MM-DD/assets.idx    hashes referenced that day
     sessions/YYYY-MM-DD/meta.json     monitor geometry
     index.db                          SQLite: day/window boundaries only
              │ reads
              ▼
   ┌──────────────────────────────────────────────────────────────┐
   │ ScreenRecall.Dashboard (WPF tray app) + PlayerCli             │
   │  timeline · scrubber · player · jump-to-focus · settings      │
   │  live resource meter · pause · purge · prune                  │
   └──────────────────────────────────────────────────────────────┘
```

## The hot path, in order

1. **Acquire.** `IDXGIOutputDuplication::AcquireNextFrame` with an adaptive timeout. Static screen →
   the timeout backs off to the configured idle poll (1 s by default). Active use → the loop blocks for
   ~0 ms and reacts to every present.
2. **Rects.** `GetFrameDirtyRects` and `GetFrameMoveRects` give the exact changed regions. A move dirties
   both the destination *and* the vacated source.
3. **Cells.** Rects expand into **absolute virtual-desktop grid cells** (64 px), not rect-relative tiles. A
   monitor whose origin isn't a multiple of 64 — the normal case in a multi-monitor setup — still shares
   cell boundaries with its neighbour, which is what makes the same visual element hash identically on
   any day and dedupe. Dirty and move rects are unioned in a `TileCellSet`, so a cell covered by three
   overlapping rects is processed once.
4. **Hash + compare.** Each touched cell is copied out of the (possibly padded, possibly DPI-scaled) frame
   buffer and hashed with xxHash3 over its pixels plus its dimensions. Only cells whose hash differs from
   the in-memory canvas are logged: dirty rects are conservative and routinely cover tiles whose pixels
   never changed.
5. **Dedupe + store.** A new hash is checked against a bounded in-memory cache and then the store. Only
   genuinely new tiles are QOI-encoded and handed to the writer thread; everything else costs an 8-byte
   reference.
6. **Log.** Entries are appended to a buffered writer in whole 27-byte records, so a reader only ever sees
   complete records — which is what lets the dashboard scrub *today's* session while it is still being
   recorded.
7. **Checkpoint.** Every `checkpointSeconds` (default 180) the canvas is written out in full with an index
   row — the equivalent of a video keyframe, and what makes seek O(replay since the last checkpoint)
   rather than O(session).

## Decisions worth knowing

**Assets are written off the capture thread.** Measured here, creating one small file (temp write + atomic
rename) costs ~5 ms with real-time scanning enabled, while hashing a tile costs 45 µs and compressing it
88 µs. Doing that inline capped capture at ~200 tiles/s and pinned a core. The writer thread drains a
bounded queue, which gives natural backpressure, and `FlushSessionState` drains it *before* flushing the
log, so a log entry can never become durable before the tile it references. That ordering makes the store
crash-consistent at every flush boundary.

**Log entries are written only for real changes.** Together with checkpoints this is the biggest lever on
size: a static screen costs nothing (DXGI reports no presents), and a moving window costs only the tiles
that actually changed.

**Cursor and DRM handling is explicit.** Pointer updates arrive as metadata, never as dirty rects, so a
pointer-only frame is skipped. A present with no rect list at all triggers a full-surface rescan that is
*rate limited but never dropped*: the engine remembers a rescan is owed, so a later dirty-rect frame can
never log a screen state that silently omits the changes it missed. This was found by the fidelity
harness, not by reasoning.

**Privacy is enforced before any pixel is read.** If the foreground window matches the exclusion list,
nothing is recorded for that period, and a full rescan follows once it loses focus — the log stays
truthful without ever containing the excluded window's content. DRM-blanked frames are skipped the same
way.

**One writer per store root.** `StoreLock` takes an exclusive lock file. Two capture processes in one root
would interleave log appends (each handle writes at its own position), so the second refuses to record
rather than corrupting what is already there.

**Writers are integrity-checked.** The log writer serializes its state (flush is reachable from control
paths while the capture loop appends), drops any all-zero record instead of persisting a hole, and
compares the file length against what it wrote to detect a foreign writer. Those guards exist because an
earlier revision of this code silently wrote 300 zero records into the middle of a log — see
`docs/VALIDATION.md` for the story and the regression tests that keep it fixed.

## Running for a day

A 24-hour session is the normal case, not an edge case, so everything that would otherwise grow all day has
an owner and a cadence. None of it runs inside `ProcessFrame`: the sweeps are cheap, bounded, or off-thread.

| Every | What | Why it exists |
|---|---|---|
| 2 s | log flush, writer drained first | the durability window, and the crash-consistency ordering rule |
| 20 min | abandoned `*.part` sweep (asset temp folder, checkpoint folder) | a crash leaves partial files that nothing else would ever claim |
| 30 min | ground-truth dump sweep | dumps are debug data with a frame and byte cap per folder; with test mode off the folder must stay empty |
| 3 min | `wal_checkpoint(TRUNCATE)` + `incremental_vacuum` on `index.db` | the WAL is a fixed-size file that would otherwise keep every page the day ever wrote |
| 6 h | retention prune (off-thread) | removes old days, their assets and their index rows |
| session start | `PrepareSessionStorage`, dump-store `ClearAll`, daily budget check | start from a known state instead of inheriting yesterday's leftovers |

Rules that keep the numbers honest:

- **A size is measured from the files, not from the directory listing.** NTFS refreshes a directory entry's
  size field when the file is *closed*, so a listing reports a log that is being appended to at the size it
  had when it was created. `SessionLayout.SessionBytes` stats each file for that reason; the asset store can
  take the cheaper listing path because assets are immutable once written (`File.Move` from a finished temp
  file, never appended to afterwards).
- **Status numbers are cached and refreshed off-thread.** Walking a store with hundreds of thousands of
  tiles is not something a two-second status poll may do, so `AssetStats()` and `SessionBytes()` return the
  last value and recount at most every 30 seconds, single-flighted. The first call answers synchronously (the
  store is small at startup) and the capture loop keeps both values warm, so a consumer that polls rarely
  still sees a number at most one interval old rather than one poll old.
- **Housekeeping failures are reported, never fatal.** Every sweep is wrapped: a busy file, a permission
  problem or a locked database becomes `LastError` in the status and is retried on the next cadence, because a
  long run has to survive a transient, not restart because of one.

Handles need a footnote. Sampled from outside the process, a real DXGI session sits at ~341 handles at
startup, peaks near ~950 during the first 90 seconds and then stays flat at 346–355 for as long as it runs;
the synthetic source (no D3D device, no duplication) never shows the peak at all. That is a warm-up
transient, not a leak — which is why `--soak` judges slopes from its second sample onward.

## Deliberate deviations from the spec

| Spec | What this does | Why |
|---|---|---|
| Hashing "can run on the GPU via a compute shader" | CPU xxHash3 per tile | Keeps the pipeline testable without vendor shaders; 45 µs/tile. A GPU hash is a drop-in behind the same interface if the budget needs it. |
| §5.6 gives a sketch of the checkpoint record | Documented, versioned 32-byte per-monitor header (`docs/STORAGE-FORMAT.md`) | The sketch omitted a reserved word; the version byte lets the format evolve. |
| Dashboard "never opens the log/asset files while the service is writing them" | Readers open with `FileShare.ReadWrite` and consume only whole records | The concern was corrupting writes; the writer emits complete aligned records, so reading a prefix is safe and enables live scrubbing. |
| Storage-path change "triggers a background migration" | The service switches roots immediately and starts a fresh session there; nothing is moved | A half-finished migration is worse than none. Copy-verify-repoint is on the roadmap and the UI says so. |
| Encryption at rest default-on | Not implemented; flag reserved and shown disabled | Stage 7 of the spec's own build order. Documented rather than pretended. |

## Threading

| Thread | Owns | Notes |
|---|---|---|
| capture thread | DXGI duplication, canvas, log writer | Below Normal + background mode; only thread that reads pixels |
| writer thread | asset store writes | Below Normal; bounded queue; drained before log flushes |
| IPC accept loop + per-connection tasks | command handling | one task per connected client |
| dashboard timers | status polling (2 s), playback (100 ms) | separate process; never writes to the store |

State that crosses threads (log writer, manifest, canvas, store root switching, pause flag) is guarded by a
lock or confined to the capture thread.
