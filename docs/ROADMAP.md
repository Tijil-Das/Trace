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
- **Nested Remote Desktop sessions** may double-capture; not special-cased yet.
- **Hash collisions**: xxHash3 is non-cryptographic. A collision would mean a tile renders as other content
  that hashed the same; `verify --content` re-hashes stored tiles and reports such cases, and the store layout
  does not depend on collision resistance for correctness (only on it being rare).
- **Crash window**: an entry is durable only at flush boundaries (≈2 s). A hard crash loses at most the last
  interval — and loses it consistently (neither the entries nor their tiles), which is the deliberate
  trade-off described in `docs/ARCHITECTURE.md`.

## Engineering notes for whoever picks this up

- The hot path is `CaptureEngine.ProcessFrame` → `ProcessTiles`. Keep allocations out of it; `_cells`,
  `_tileScratch` and the canvas are reused on purpose.
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
