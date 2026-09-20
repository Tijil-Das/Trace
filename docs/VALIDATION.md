# Validation

Every number here was produced on the development machine: Windows 11 Pro x64 (build 26200), 4 logical
cores, 1366×768 display, .NET 8.0.425, Release builds, real-time malware scanning enabled. Anything marked
"≈" is a single-sample measurement, not a benchmark suite.

## 1. Fidelity — the number that matters

Method: `captureGroundTruth: true` makes the capture service dump a full ground-truth frame (same timestamp)
about once per second while recording normally. The verifier then seeks the player to each of those
timestamps and pixel-diffs the reconstruction against the dump.

```powershell
dotnet run --project player-cli -c Release -- fidelity .\dev-data\fidelity-validation 2026-09-20
```

| Run | Screen | Frames checked | Exact | Pixel match |
|---|---|---|---|---|
| Final | real desktop driven by a scripted activity window (moving block, typing line, scrolling band, colour cycling) | 11 | 11 | **100.0000%** |
| Earlier | same workload | 10 | 9 | 99.9610% |

The 99.96% run is what exposed the two real bugs in §5 — a one-tile hole in one frame. Both are fixed; the
current result is pixel-perfect on every frame, with 264/264 canvas tiles present.

This is *end-to-end* fidelity: grid alignment, hashing, dedupe, QOI encoding, the log format, checkpointing
and replay are all inside the measured path. The only thing it does not cover is what DXGI itself hands over.

## 2. Hot-path primitives

```powershell
dotnet run --project capture-service -c Release -- --bench 1000
```

| Primitive | Per item | Throughput |
|---|---|---|
| xxHash3 over a 64×64 BGRA tile | 45 µs | 347 MB/s |
| QOI lossless encode | 88 µs | 11.4k tiles/s |
| Asset store write (temp + atomic rename) | **5.09 ms** | 197 files/s |
| Log append (27-byte record) | 0.004 ms | 239k records/s |
| Tile read + decode + hash verify | 0.70 ms | 1.4k tiles/s |

The store-write number is the headline: on this machine, creating one small file costs **~115×** more than
compressing the tile that goes into it. That single fact drove the architecture (see §4).

## 3. Capture loop, live

```powershell
dotnet run --project capture-service -c Release -- --once 16 --root .\dev-data\capture-validation
```

Real desktop with the scripted activity window on screen:

| Metric | Value |
|---|---|
| Frames acquired | 51 (all with changes, 1 full rescan) |
| Tiles hashed | 4,336 (≈201/s) |
| Tiles stored / deduped | 1,724 / 1,386 (**32% deduped**) |
| Log entries | 3,183 |
| Frame processing | 53 ms under heavy change; 3.8 ms in the steady-state sample |
| Per-frame phases (last frame) | readback 6.2 ms, hash 2.7 ms, encode/store/log ≈0 ms (no new tiles) |
| Log integrity | 0 zero records, no foreign writer |
| Session log size | 28 KB for 18 s of dense change (27 bytes per entry) |

Read two things carefully: encode/store/log fall to ~0 ms as soon as a frame contains no *new* content, and a
static screen records nothing at all — DXGI reports no presents, the loop idles at its backoff interval, and
CPU stays under a tenth of a percent. The "53 ms per frame" figure comes from a synthetic desktop generating
~200 changes/s, not from typical use.

## 4. Why the writer thread exists

A first version wrote tiles inline. Measured then: **1.1 s per frame** on a 1366×768 screen whose top half
changed — a ~200 tiles/s ceiling with a core pinned. Moving writes to a background queue with ordering
guarantees took the same workload to:

| | Inline writes | Background writer |
|---|---|---|
| Store time per frame | 1,107 ms | 35 ms (queue hand-off) |
| Frame processing | ≈4.5 s | ≈53 ms under the same load |

The ordering rule matters as much as the speed: `FlushSessionState()` drains the writer *before* flushing the
log, so an entry is never durable before the tile it points at. If the process dies mid-interval, neither the
entries nor the tiles of that interval reached disk — the store stays consistent without fsyncs on the hot path.

## 5. Bugs the harness caught (and the tests that keep them fixed)

1. **Silent log holes.** 300 all-zero records appeared in the middle of a log, which playback reads as "clear
   the canvas" — it showed up as one tile of a frame reconstructing as a hole. Cause: `Flush()` is reachable
   from control paths (pause, purge, dispose) while the capture loop appends, so a torn buffer could be
   written. Fix: serialize writer state, drop any all-zero record instead of persisting it, and compare the
   file length against what the writer appended to detect a foreign writer. Tests:
   `LogWriterIntegrityTests` (concurrent append/flush stress, zero-record drop, external writer, store lock).
2. **Dropped rescans.** When a present arrived with no rect list, the full-surface rescan was rate limited
   *and dropped*, so a later dirty-rect frame could log a screen state missing those changes. Fix: a rescan is
   *owed*, never dropped. Test: `PipelineTests.CapturedSessionReconstructsPixelPerfectly`.
3. **Durable log ahead of durable tiles.** The async writer was drained with a timeout, so under load the log
   could reference tiles not yet on disk and reconstruction showed holes. Fix: the ordering rule in §4. Test:
   the same pipeline test plus `verify --content` over the captured session.
4. **Checkpoint size mismatch.** The hand-rolled serializer's monitor header was 2 bytes larger than its own
   constant, crashing on the first real checkpoint. Fix: paired `BinaryWriter`/`BinaryReader`, with round-trip
   tests over four monitor geometries including an unaligned second monitor and a negative origin.
5. **DXGI timeout misdetected.** SharpGen's `Result.WaitTimeout` is the Win32 code (258), not DXGI's
   `0x887A0027`, so every idle timeout looked like a lost session and rebuilt duplication, forcing a
   full-screen rescan each time. Fix: compare the raw DXGI codes.
6. **A size that was never refreshed.** The status panel and the CLI reported `0 KB session / 0 assets` for a
   session holding hundreds of kilobytes, while an in-process unit test of the same walk passed. Two causes,
   both worth keeping: the cached value was refreshed *only when someone polled*, so a consumer polling once a
   minute always read the previous answer (the soak showed `0`, `0`, then a jump), and the walk itself took
   sizes from `DirectoryInfo.EnumerateFiles`, whose size field NTFS only refreshes when a file is **closed** —
   so the log being appended to was reported at the size it had when it was created (exactly the 340 bytes the
   diagnostic printed for a 400 KB log). Fixes: stat each file in `SessionLayout.SessionBytes`, and let the
   capture loop keep the cached stats warm on its own cadence. Tests:
   `LongRunHousekeepingTests.SessionSizeCountsALogThatIsStillOpen` (appends 540 KB through an open writer and
   requires the walk to see it) and `...EngineRefreshesItsCachedSessionSizeAndAssetStatsOffThread` (requires
   the cached value to exceed the log's real length).

## 6. Test suite

```powershell
dotnet test tests\ScreenRecall.Tests\ScreenRecall.Tests.csproj -c Release
```

44 tests, all green (≈1 minute 15 seconds; the soak, stress and pipeline tests dominate). Coverage worth naming:

- **End-to-end**: synthetic desktop → real capture engine → real player → pixel diff against ground truth;
  seeking backwards reproduces the same frame as forward replay; a paused engine records nothing.
- **Format**: log entry layout byte-by-byte, writer/reader round-trip across buffer boundaries, appending to an
  existing segment, and a live reader that only ever sees complete records.
- **Geometry**: absolute grid alignment across two monitors, unaligned and negative origins, partial edge
  tiles, index/cell round-trip, dimension-sensitive hashing.
- **Codecs**: QOI bit-exact on flat and noisy content, partial tiles, balanced-mode quantization stays
  decodable, PNG header.
- **Store**: content addressing, dedupe, sorted enumeration (the invariant GC merge-walks), corrupted asset
  rejection.
- **Retention**: GC keeps referenced assets and reclaims the rest, the grace period protects fresh assets,
  pruning removes old days and their index rows, purge rewrites the log without the range.
- **Long-run housekeeping**: abandoned temp files and dump caps, WAL checkpoint + incremental vacuum on a live
  index, the daily budget switch, and the cached size numbers (a walk that sees a log which is still open, and
  a cache that refreshes without a poller).

## 7. Long-run behaviour

"Safe to leave running" is a claim about what happens over a day, so the things that grow are measured rather
than assumed. `--soak` runs the real engine and IPC path, samples on an interval and ends with a verdict:

```powershell
dotnet run --project capture-service -c Release -- --soak 12 --soak-interval 30 --root .\dev-data\soak
```

| Metric | 12-minute run, real desktop | Verdict |
|---|---|---|
| CPU average (after warm-up) | 6.0% | **over** the spec's 2% budget, on this 4-core machine, while the desktop was actively changing |
| Working set | 32 MB → 32 MB | flat |
| Ground-truth dumps | 0 files, 0 MB (test mode off) | empty, as the 30-minute sweep requires |
| Abandoned temp files | 0 | swept every 20 minutes |
| Index WAL | 20 KB after 0.2 h | checkpointed every 3 minutes |
| Asset queue | ≤ 212 payloads pending | bounded: backpressure, not growth |
| Store | 971 KB session, 81 MB of assets in 27,745 files | heavy-change desktop → 404 MB/hour of new tiles, 4.7 MB/hour of session |

Handles get their own paragraph, because the first version of this measurement was wrong in a way worth
recording: reading `Process.HandleCount` *inside* the process reported up to 2,682 handles for a process that
an external observer saw holding ~350 at the same time. `--soak` now calls `GetProcessHandleCount` directly —
the same system call Task Manager uses — and the two agree. Measured that way, on a real desktop with the
screen changing: ~340 at startup, 346–364 for minutes at a time, occasional waves up to ~971 lasting a few
hundred milliseconds, GDI and USER object counts flat at 0–2, and the synthetic source (no D3D device, no
duplication) steady at ~300. The *floor* never rises, so nothing accumulates over a day — which is the property
the verdict checks, with the slope judged from the second sample onward so warm-up cannot be mistaken for a
leak. The source of the waves is not pinned down: it is not the store walks (an isolated walk over 256
directories and 5,120 files holds one extra handle) and not win32k objects, and it is on the roadmap to
identify rather than guess at.

Shutdown is bounded as well. The loop drains queued tiles before flushing the log — an entry must never become
durable before the tile it points at — but only for 15 seconds: if the disk stalls, that interval is skipped
wholesale and the reason is reported, because a recorder that cannot be stopped is worse than one that loses
an interval it was already going to lose. The pipeline tests found this the hard way: a test that outruns the
writer can have a full 2048-payload queue to write out when the token is cancelled.

The 404 MB/hour figure is the expensive case on purpose: a desktop that changes continuously is what a daily
budget has to survive, and `--soak` prints the rate instead of a single size so the budget can be set against
it. A static screen is the other extreme — DXGI reports no presents, the loop backs off and nothing is
recorded at all (§3) — so the number to plan with is the moving desktop, not the average.

## 8. How to reproduce

```powershell
# fidelity, with a scripted on-screen workload so there is something to record
Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','.\dev-data\screen-activity.ps1','-Seconds','18'
dotnet run --project capture-service -c Release -- --once 12 --config .\dev-data\fidelity-config.json
dotnet run --project player-cli   -c Release -- fidelity .\dev-data\fidelity-validation (Get-Date -Format yyyy-MM-dd)

# integrity of everything that session references
dotnet run --project player-cli -c Release -- verify .\dev-data\fidelity-validation (Get-Date -Format yyyy-MM-dd) --content

# seek cost: nearest checkpoint + replay, cold tile cache
dotnet run --project player-cli -c Release -- bench-seek .\dev-data\fidelity-validation (Get-Date -Format yyyy-MM-dd) --iterations 20
```

`dev-data/fidelity-config.json` is just a `RecallConfig` with `captureGroundTruth: true` and a storage path;
any config file works. Both `dev-data` and `dev-data/*` are gitignored — captured screen content must never be
committed.

## 9. What these numbers do *not* prove

- **Not an 8-hour soak.** The spec's §12 asks for an 8+ hour run with CPU/GPU sampled every 30 s. `--soak`
  measures the same quantities and prints a verdict, but the longest run recorded here is 12 minutes; the
  8-hour run is wall-clock time, not code. GPU is unused by design (CPU hashing), so the spec's 5–20% GPU
  budget is unspent rather than measured.
- **Single monitor.** Everything was measured on one 1366×768 display. Multi-monitor geometry and partial
  edge tiles are unit-tested but not measured end to end.
- **Real-time scanning inflates the I/O numbers.** A store-directory exclusion would improve them
  substantially; the architecture deliberately does not depend on one being present.

