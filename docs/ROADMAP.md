# Roadmap and known gaps

Mapped against the spec's suggested build order (§16). Nothing here is silently skipped: what is missing is
listed as missing.

## Done

| Spec step | State |
|---|---|
| 1. Capture service MVP: DXGI loop → dirty rects → raw log | Done. `--probe` and `--once` validate it on any machine with a duplicatable output. |
| 2. Tiling, hashing, content-addressable dedupe | Done. Absolute-global grid, xxHash3, sharded store, bounded dedupe cache, background writer. |
| 3. Checkpointing + minimal CLI player | Done. `player-cli` renders frames, exports sequences, benchmarks seeks, runs the fidelity and integrity harnesses. |
| 4. Dashboard shell wired to the service over IPC | Done as a runnable dev-mode app: day list, scrubber, playback, jump-to-focus, live resource meter, settings, pause/purge/prune, tray icon, global hotkey. |
| 5. Settings (storage path, retention, exclusions) + pruning job | Settings and the pruning job are done; **storage-path migration is not** (see below). |
| Optional text index / MP4 export | Not started — optional in the spec. |
| 6. Installer, first-run wizard, code signing | **Intentionally not started.** The project owner asked for no packaging until the dev-mode build has been tested and signed off. |
| 7. Encryption at rest | Not started — see below. |
| Long-run hardening (24 h+) | Done as far as measurement goes: bounded ground-truth store with sweeps, temp-file sweep, WAL checkpoint + incremental vacuum, daily budget enforcement, manifest hash-tracking cap, cached off-thread size stats, and a `--soak` command that samples the things which grow and prints a verdict. See `docs/ARCHITECTURE.md` §"Running for a day" and `docs/VALIDATION.md` §7. |

## Next, in the order I would do them

1. **Human pass over the dashboard.** It builds and is wired to the service through validated IPC, but a GUI
   needs eyes on it: layout at different DPI/scales, playback smoothness on a real session, tray behaviour.
   Everything below the UI is already covered by tests and CLI harnesses.
2. **Encryption at rest (spec §6 / §14).** `RecallConfig.EncryptionAtRest` exists and is shown disabled in the
   UI on purpose. Design to land: per-store data key, wrapped with DPAPI (current user) or an optional
   passphrase, AES-256-GCM envelopes around tile payloads and log chunks, key material never in the config
   file. The asset header already carries a codec byte, so an encryption envelope is a versioned addition
   rather than a format break. Needs a decision on the unlock UX for a service running as LocalSystem.
3. **Storage-path migration (spec §6).** Currently the service switches to the new root immediately and starts
   a fresh session there; nothing is copied. The intended implementation is copy → verify (file count, byte
   total, sampled hash checks) → repoint → delete old, with progress surfaced over IPC and a hard stop on any
   verification failure. The dashboard already tells the user when the path changed.
4. **8-hour soak + resource sampling.** `--soak [minutes] [--soak-interval s]` now measures what the spec's
   §12 asks about — CPU, working set, managed memory, handles, GDI/USER objects, store and session growth,
   dumps, temp files, WAL and queue depth — and ends with a per-metric verdict, so a long run is a
   measurement rather than a hope. A 12-minute real-desktop run is recorded in `docs/VALIDATION.md` §7.
   Still missing: the 8-hour run itself (wall-clock only), GPU counters (GPU is unused by design), and
   in-run alerts when a metric crosses its budget instead of discovering it at the end.
5. **Exclusion-list polish.** Defaults cover password managers and detectable private/incognito windows. Not
   yet done: per-window granularity (excluding one window instead of everything while it is focused), and a UI
   that shows what is currently being skipped with a live preview.
6. **Multi-monitor end-to-end validation.** Geometry, unaligned origins and partial tiles are unit-tested, but
   no machine with two displays was available here.
7. **Text-index module (spec §7, off by default)** and **MP4 export (occasional output)** — both explicitly
   optional and additive; neither touches the pixel path.
8. **Installer (WiX/MSIX), first-run wizard, code signing** — only once the owner green-lights packaging.

## Known limitations (inherited from the spec, restated)

- **DRM-protected video** renders black in both capture and reconstruction; blocked by HDCP at the compositor.
  Such frames are detected (`ProtectedContentMaskedOut`) and skipped rather than recorded as black.
- **Exclusive-fullscreen apps** that bypass the compositor may not be captured; flagged in the UI rather than
  fought.
- **Secure Desktop surfaces** (UAC, lock screen, Ctrl+Alt+Del) are un-capturable by any user-mode app by
  design, and this code does not attempt to work around that.
- **Nested Remote Desktop sessions** that are connected capture normally; a *disconnected* session is reported as
  idle (`SessionDisconnected`) rather than captured, and capture resumes automatically on reconnect.
- **Hash collisions**: xxHash3 is non-cryptographic. A collision would mean a tile renders as other content
  that hashed the same; `verify --content` re-hashes stored tiles and reports such cases, and the store layout
  does not depend on collision resistance for correctness (only on it being rare).
- **Crash window**: an entry is durable only at flush boundaries (≈2 s). A hard crash loses at most the last
  interval — and loses it consistently (neither the entries nor their tiles), which is the deliberate
  trade-off described in `docs/ARCHITECTURE.md`.
- **A live view lags under heavy change.** The flush ordering means the log cannot run ahead of the tiles, so
  while a source outruns the asset writer (a synthetic desktop at full speed, or a desktop changing many tiles
  per frame) the visible log can trail by seconds, and a reader may show the running day as nearly empty.
  Finishing the session or reading a past day is accurate. A "durable watermark" that lets the log flush up to
  the tiles already written would remove the lag without weakening the ordering rule; it is not implemented.

## Engineering notes for whoever picks this up

- The hot path is `CaptureEngine.ProcessFrame` → `ProcessTiles`. Keep allocations out of it; `_cells`,
  `_tileScratch` and the canvas are reused on purpose.
- **A capture-loop change is not done until its Release-build CPU has been measured.** Run `Trace.cmd bench 5`
  and record the `cpu-bench result:` line. `docs/PERFORMANCE.md` §3 is the checklist and §4 is the list of
  design decisions that currently risk the < 2% target (spec §3) — read §4 before touching the loop, not after.
- The synthetic desktop is **test-only**: `--synthetic-test` is the only way to run it, and it is also aliased as
  `--synthetic`. Nothing in the service ever selects it on its own. When no display can be duplicated the service
  goes idle (`no-output`), retries on a backoff, and says so over IPC — the dashboard shows a red icon and a clear
  sentence, not a green light. `docs/PERFORMANCE.md` §4 explains why this is also the biggest CPU line item.
- **A session's `meta.json` can hold more than one geometry for one monitor id** (a mode change, a re-plug, or a
  fallback source that reused id 0). Two rows mean two tile grids, and tile (x, y) is different pixels under each.
  Anything that applies a log entry must resolve the row in force **at that entry's timestamp** via
  `SessionMeta.MonitorAt`/`MonitorsAt`; `AllMonitors()` is only for "which geometries exist" questions (counts,
  day summaries, seeding a canvas). Getting this wrong replayed a 689 MB session as repeating stripes — see
  `docs/PERFORMANCE.md` §4.8.
- **The tile encoder is not the CPU problem, and is not going to the GPU.** A 30 s `dotnet-trace` of a live DXGI
  recording puts `QoiCodec.EncodeInto` at 2.3% of capture-thread samples against 23% for the per-tile phase
  instrumentation and 13% for the dedupe probe's filesystem path. QOI's per-tile state machine is strictly
  sequential, so a compute-shader port would be research-grade work for ~2%. `docs/PERFORMANCE.md` §5a has the
  numbers, including why the archive codec must never become the capture codec (12× slower).
- Never log an entry for a tile whose hash equals the canvas: that identity is what keeps both the log and the
  store small, and `PipelineTests` will catch it if the invariant breaks.
- Never flush the log without draining `AssetWriteQueue` first — see the crash-consistency rule.
- `FidelityVerifier` is the fastest way to catch a regression: it compares the real pipeline against
  ground truth, so a change that silently loses tiles fails a test instead of quietly degrading playback.
- Long-run behaviour is measured, not assumed. `--soak` samples the things that grow and prints a verdict, and
  every housekeeping cadence lives in one file (`CaptureEngine.Housekeeping.cs`). Three rules came out of it:
  never flush the log until the writer has drained (checkpoints too — they are the strongest claim a session
  makes), never let shutdown wait for the writer forever (15 seconds, then that interval is skipped rather than
  half-written), and never measure handles with `Process.HandleCount` (it reported 2,682 for a process holding
  350 — use `GetProcessHandleCount`).
