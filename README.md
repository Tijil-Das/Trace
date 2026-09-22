# Screen Recall

A locally-installed Windows app that continuously records what is visibly drawn on the screen — not as
video, but as a **content-addressable tile cache plus a timestamped reference log**, the same principle
RDP uses to send a usable desktop over kilobits — and reconstructs exact frames on demand.

This repository implements the engineering specification in
[`personal-screen-recall-spec.md`](personal-screen-recall-spec.md). It is at **build-order stages 1–4**
(see [`docs/ROADMAP.md`](docs/ROADMAP.md)): the capture service, storage subsystem, player and a
dashboard all exist and run; packaging is deliberately not done yet (see *Status*).

## Status at a glance

| Piece | State |
|---|---|
| Capture service (DXGI duplication) | Working: adaptive cadence, dirty/move rects, 64px grid tiling, xxHash3, dedupe, QOI, checkpoints, exclusions, pause, self-throttling, named-pipe IPC, Windows-service or console mode |
| Storage subsystem | Working: content-addressable store, per-day reference log, checkpoints, per-day asset manifests, SQLite navigation index, retention pruning + asset GC, panic purge |
| Player | Working: checkpoint + replay seek, reconstruction, PNG export, fidelity harness, integrity check, seek benchmark |
| Dashboard | Working shell: day list, scrubber, play/pause/speed, frame view, jump-to-focus, live resource meter, settings, pause/resume, purge and prune, tray icon, global hotkey |
| Tests | 44 xUnit tests green, including an end-to-end capture→replay→pixel-diff test |
| Encryption at rest | **Not implemented** (interface reserved; see ROADMAP) |
| Text index, MP4 export | **Not implemented** (spec §7, optional) |
| Installer (WiX/MSIX) | **Not started on purpose** — no packaging until the dev-mode build has been tested and signed off, per the project owner's instruction |

Measured on the development machine (1366×768, 4 logical cores, real-time AV scanning on):

| Metric | Result |
|---|---|
| Reconstruction fidelity | **100.0000% pixel match** across every ground-truth frame checked (11/11, 264/264 tiles) |
| Steady-state CPU (release, real screen) | ~0% while idle (no presents), low single digits during typing/scroll bursts |
| Tile hashing | 45 µs per 64×64 tile (347 MB/s) |
| QOI encode | 88 µs per tile |
| Asset write (cold store) | ~5 ms per new file — the reason writes run on a background thread |
| Log append | 0.004 ms per entry (238k entries/s) |

Full numbers, methodology and the exact commands are in [`docs/VALIDATION.md`](docs/VALIDATION.md).

## Repository layout

```text
storage-lib/      ScreenRecall.Storage   shared: asset store, log format, checkpoints, index, pruning
capture-service/  ScreenRecall.CaptureService  Windows service: DXGI loop, tiling, hashing, dedupe, IPC
player-lib/       ScreenRecall.Player    reconstruction engine: seek, replay, render, diff, verify
player-cli/       ScreenRecall.PlayerCli minimal CLI player + fidelity/integrity harnesses
dashboard-app/    ScreenRecall.Dashboard WPF tray app: timeline, player, settings, resource meter
tests/            ScreenRecall.Tests     xUnit suite (each test uses its own throwaway store)
docs/             architecture, storage format, validation, roadmap
```

## Running it in dev mode (no installer)

Everything runs from source. There is no service registration, no MSI, and nothing is written outside the
folders you name.

```powershell
# 0. one-time: restore/build everything
dotnet build ScreenRecall.sln -c Release

# 1. check what the machine offers (duplication, monitors, grid)
dotnet run --project capture-service -c Release -- --probe

# 2. record for 20 seconds into a throwaway folder and print the numbers
dotnet run --project capture-service -c Release -- --once 20 --root .\dev-data\demo

# 3. no display handy? the synthetic desktop exercises the exact same pipeline
dotnet run --project capture-service -c Release -- --once 10 --synthetic --root .\dev-data\demo

# 4. browse and replay it
dotnet run --project player-cli -c Release -- list   .\dev-data\demo
dotnet run --project player-cli -c Release -- info   .\dev-data\demo 2026-09-20
dotnet run --project player-cli -c Release -- render .\dev-data\demo 2026-09-20 --at 12:04:31.220 --out frame.png
dotnet run --project player-cli -c Release -- verify .\dev-data\demo 2026-09-20 --content
dotnet run --project player-cli -c Release -- bench-seek .\dev-data\demo 2026-09-20

# 5. live mode: leave the service running in the foreground, then open the dashboard
dotnet run --project capture-service -c Release -- --console --root .\dev-data\live
dotnet run --project dashboard-app  -c Release
```

The service listens on the named pipe `ScreenRecall.Capture`; the dashboard uses it for status, pause,
purge, prune, probe and settings, and reads historical sessions from disk. Closing the dashboard window
leaves it in the notification area and does not affect recording.

To register the service for real (needs an elevated shell) the executable prints the exact `sc.exe`
commands:

```powershell
dotnet run --project capture-service -c Release -- --service-commands
```

## Testing

Prerequisite: the .NET 8 SDK. On this development machine the `dotnet` on `PATH` (`C:\Program Files\dotnet`) is
a *runtime-only* install — `dotnet --list-sdks` prints nothing and every `dotnet build/test/run` fails with
"No .NET SDKs were found". The SDK lives in `%LOCALAPPDATA%\Microsoft\dotnet`; prepend it (or call it by full
path) before running anything:

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet --version   # 8.0.425
```

| What | Command | What good looks like |
|---|---|---|
| Unit + integration suite (44 tests) | `dotnet test tests\ScreenRecall.Tests\ScreenRecall.Tests.csproj -c Release` | `Passed! - Failed: 0, Passed: 44` in 1–2 minutes; every test uses its own throwaway store under `%TEMP%` |
| Hardware check | `dotnet run --project capture-service -c Release -- --probe` | each output and monitor listed, `duplication : ok`, 60 acquired frames |
| Quick record + replay | `dotnet run --project capture-service -c Release -- --once 20 --root .\dev-data\demo`, then `dotnet run --project player-cli -c Release -- list .\dev-data\demo` | the capture summary table, then the day listed with log span, store size and checkpoints |
| Same, with no display | add `--synthetic-test` to `--once` | identical pipeline on a software desktop (test-only: it records injected frames) |
| Hot-path budgets | `dotnet run --project capture-service -c Release -- --bench 2000` | microseconds per tile hash/encode, milliseconds per store write, microseconds per log append |
| Long-run check | `dotnet run --project capture-service -c Release -- --soak 30 --soak-interval 30 --root .\dev-data\soak` | per-interval samples, then a per-metric verdict (`[ ok ]` / `[ !! ]`); `--soak 1440` is the spec's 24-hour version |
| CPU budget (spec §3) | `Trace.cmd bench 5` | a verdict per metric, then one `cpu-bench result:` line to record; exit 0 within budget, 1 over, 2 invalid measurement |
| Lost capture (locked session, 2nd instance) | the service goes idle, the tray turns red, and the dashboard says so | no silent anything: check `state` over IPC (`recording` / `paused` / `no-output`) |
| End-to-end fidelity | see *Fidelity harness* below | pixel match 100.0000% |

Two things worth knowing when reading the output:

- **A live session's log lags behind on purpose.** Entries only become durable after the tiles they reference
  are on disk, so a source that outruns the writer (`--synthetic` at full speed) can hold the log in memory for
  seconds at a time. A reader — the player, the dashboard, another CLI — can therefore show a *running* day as
  empty or as ending a few seconds ago. That is the documented crash window, not corruption: stop the recorder,
  or look at a finished day.
- **Stop a running recorder before building.** `dotnet build` has to replace
  `capture-service\bin\...\ScreenRecall.CaptureService.exe`, and a live instance locks it
  (`MSB3027: ... The file is locked by: ScreenRecall.CaptureService`). The test suite is no exception.

Nothing you capture leaves the machine: `dev-data/`, `.dev-logs/` and `*.srdata` are gitignored.

## Fidelity harness

The spec's §12 harness is implemented end to end: set `"captureGroundTruth": true` in the config, record a
session, then compare the player's reconstruction against the ground-truth frames the service dumped for
the same instants.

```powershell
dotnet run --project player-cli -c Release -- fidelity .\dev-data\fidelity-validation 2026-09-20 --dump .\dev-data\fidelity-dump
```

Result on this machine: *11 frames checked, 11 exact, pixel match 100.0000%*.

## Documentation

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — components, threading, the hot path, and why each
  decision was made (including deliberate deviations from the spec).
- [`docs/STORAGE-FORMAT.md`](docs/STORAGE-FORMAT.md) — byte-level formats for the log, assets,
  checkpoints, manifests and ground-truth dumps.
- [`docs/VALIDATION.md`](docs/VALIDATION.md) — measurements, harnesses, and how to reproduce them.
- [`docs/PERFORMANCE.md`](docs/PERFORMANCE.md) — the CPU budget from spec §3: how it's measured, the definition
  of done for capture-loop changes, and the design decisions that currently put it at risk.
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — what is done, what is deliberately deferred, and the known gaps.
