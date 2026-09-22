# Performance budget (spec §3)

The spec has exactly one hard, non-negotiable performance requirement: **steady-state CPU below 2%** (§3),
measured as P95 over a long run (§12). This page is the contract for it — what the number means, how to
measure it, what "done" means for a capture-loop change, and which design decisions currently put it at risk.

Bloat is not the concern here; drift is. A capture loop gets faster by accident (fewer tiles) as easily as it
gets slower, so the target has to be a standing check with a recorded number, not a memory of one.

## 1. The budget

| Metric | Target | Source |
|---|---|---|
| Steady-state CPU, active use | **< 2%** average of total machine CPU | spec §3 |
| Steady-state CPU, 8+ hour run | P95 < 2% | spec §12 |
| Static screen | **< 0.5%** (project rule, deliberately tighter) | this page |
| GPU | 5–20% acceptable, unused by design (CPU hashing) | spec §3 |
| Compression skipped | **> 80%** of hashed tiles | spec §5.3 |

CPU is quoted the way Task Manager quotes a process: process CPU time divided by wall time **and by the
logical core count**, so 100% means every core busy and one saturated core reads 25% on a four-core machine.
`CaptureStats.SampleCpuPercent` and `--cpu-bench` both use that convention; a figure quoted any other way is
not comparable to the budget.

The static-screen rule is tighter than the spec on purpose. The spec's 2% has to cover *active* use; a recorder
that spends most of its allowance while nothing is happening has already spent the budget.

## 2. How to measure

```powershell
Trace.cmd bench 5                                              # Release, 5 min idle + 5 min active, logged
Trace.cmd bench 0.25                                           # harness smoke test (~25 s in total)
dotnet run --project capture-service -c Release -- --cpu-bench 5 --activity none   # measure real use
```

`--cpu-bench [minutes]` measures two phases back to back in one process and prints a verdict:

| Check | Passes when |
|---|---|
| build configuration | the running binary is Release (JIT optimizations on) |
| static-screen CPU | idle-phase average < 0.5% |
| screen was static | at most 10 presents during the idle phase (0 is ideal) |
| active-use CPU | active-phase average < 2% |
| active use observed | the active phase hashed at least one tile |
| compression skipped | > 80% of hashed tiles never reached the encoder |
| errors | the engine reported no error in either phase |

Exit code **0** = within budget, **1** = over budget, **2** = the measurement is not valid input to the budget
(a Debug build, a machine that was not idle, or a run that recorded nothing). The last line of the output is the
one to keep:

```text
cpu-bench result: idle=0.012% active=1.842% target=2.000% idle-presents=0 dedupe=93.1% verdict=PASS
```

Things worth knowing before reading a number:

- **Ten seconds of warm-up are excluded** from both phases (JIT, the first full rescan, the stat walks that seed
  the store figures). They are real costs, but they are not steady-state ones.
- **Phase 1 needs a genuinely static screen.** Presents during it are reported, not hidden: a taskbar clock or a
  tray icon is tolerated, a stream of them is not, because every present forces a full-surface readback and a
  rate-limited rescan. If the check fails, the answer is to run it again with your hands off the machine.
- **The active phase is scripted by default** (`dev-data/screen-activity.ps1`, the fidelity harness window), which
  makes repeat runs comparable but is a *ceiling*, not a forecast: it drives a moving block, a typing line and a
  colour cycle as fast as the compositor will take them. `--activity none` measures whatever you actually do.
- **A Debug build cannot produce evidence.** All the performance-facing verbs (`--bench`, `--once`, `--soak`,
  `--cpu-bench`) say so in their output; `--cpu-bench` additionally refuses to certify the run. Rebuild Release
  first — a Debug number is a different program.
- **The measured process runs under the shipped governance** (below-normal priority, Windows background mode —
  spec §5.8), because that is the configuration whose CPU has to fit the budget. A number taken with the process
  promoted to normal priority is not the same measurement.

## 3. Definition of done — capture-loop changes

A change to the capture loop is not done until this checklist is answered. Copy it into the PR or the commit
message; the point is that it is *asked*, not that it is long.

- [ ] **Release build.** `Trace.cmd build`, then run the exe from `capture-service\bin\Release\...`. Never quote
      a Debug figure as evidence.
- [ ] **Budget measured, both phases.** `Trace.cmd bench 5` on a real (unlocked) desktop, result line recorded
      below or in the commit. Idle < 0.5%, active < 2%, dedupe > 80%.
- [ ] **The verdict's own gates pass.** "screen was static" and "active use observed" both `ok` — a run with
      either failing is not a cheap recorder, it is a mis-measurement.
- [ ] **Counters still balance.** `compression skipped` is in the same range as before, and `--once` reports 0
      zero-records and no external writer.
- [ ] **Allocations.** Nothing new on the per-frame path (`ProcessFrame` → `ProcessTiles` → codecs). Verify with
      `GC.GetAllocatedBytesForCurrentThread` around the change rather than by reading the code — the existing
      per-tile hash allocation (§4.5) was invisible in review.
- [ ] **Tests green.** `Trace.cmd test` — `PipelineTests` catches the canvas/dedupe invariants, `LongRunHousekeepingTests`
      catches cadence regressions.
- [ ] **Regression recorded honestly.** If the number went up and the change is worth it, say so with the
      measurement, and update §5. A budget that is only checked when it flatters the change is not a budget.

## 4. Design decisions that risk the budget

Reviewed 2026-09-22 with the evidence available at that time: a live Release recorder's own counters, the
`--cpu-bench` smoke run, and one focused probe (`.dev-logs/hashprobe`). Ordered by how large the risk looked, not
by how easy it is to fix.

**All seven were addressed in one pass.** The numbers moved from ~16-18% of the machine to the figures in §5;
the evidence for each is kept below because the reasoning is what a future change has to argue with, not the
number it happened to produce.

| # | Risk | Status | Where the fix lives |
|---|---|---|---|
| 4.1 | The adaptive cadence could not slow the loop down on a chatty desktop | fixed | `AdaptiveCadence.NextDelay`, called from the loop |
| 4.2 | A full-surface, synchronous readback on every frame | fixed (CPU side; the GPU blit is deliberate) | `ReadbackRegions`, `BufferMapper.CopyRegionsToPacked` |
| 4.3 | The capture thread drained the whole asset queue every two seconds | fixed | `AssetWriteQueue.WaitForEmpty`, `FlushSessionState(deferIfBusy)` |
| 4.4 | Most of the process's CPU was outside the per-frame instrumentation | closed — profiled §5a (the instrumentation is itself the top cost) | `PaceIntervalMs`, dedupe cache counters, source block in status |
| 4.5 | One heap allocation per tile hash, and 1.8x slower than necessary | fixed | thread-static hasher in `TileHash` |
| 4.6 | The dedupe cache forgot everything at once, turning misses into filesystem probes | fixed | two generations in `DedupeCache` |
| 4.7 | Two store walks run from the hot loop | open | — |
| 4.8 | Replay drew one geometry's tiles on another geometry's grid — repeating stripes | fixed | `SessionMeta.MonitorAt`/`MonitorsAt`, `SessionReplayer.SyncGeometry` |
| 5a | Per-tile phase instrumentation (23% of the capture thread), the dedupe probe path (13%), the drain wait (21%) | open, ranked by a 30 s trace | sample the phase timers; seed `DedupeCache` from the manifest |

### What the governor and the readback changed, in numbers

| Measurement | Before | After |
|---|---|---|
| Loop pace on a chatty desktop | none (0 ms acquire timeout, throttle pinned at 4, no effect) | duty-cycle pause, `PaceIntervalMs` reported; measured 250 ms at 21 ms/frame |
| Readback per frame | whole surface copied to CPU (4.2 MB at 1366×768, 33 MB at 4K) | dirty regions only, snapped to tiles |
| Flush stall | capture thread waited out the whole queue every 2 s | deferred past 512 queued payloads; drains signal instead of polling |
| Per-tile hash allocation | 568 bytes | 0 bytes (thread-static `XxHash3` + `Reset`) |
| Dedupe cache | cleared everything at 262,144 entries | two generations, rotated |
| Synthetic fallback | recorded invented frames at 60 Hz (~18% of the machine) | impossible without `--synthetic-test`; idle + classified retry instead |


Not on the original list, and larger than everything on it. When DXGI could not be opened - and on this machine it
could not, because the session was locked, which is Windows working as designed - `CaptureWorker.CreateSource` fell
back to the timer-driven synthetic desktop. That records *injected* frames at 60 a second: a recording that is not
a recording, and the single biggest CPU cost in the service, measured at **18.3% of a four-core machine** while
nothing at all was happening on screen.

It is gone. `RecoveringDxgiSource` never substitutes anything: with no capturable output the loop idles (the
acquire returns immediately, the cadence backs off to its idle poll) and retries DXGI every 5 s, 10 s, 20 s, 40 s,
then once a minute. The reason is classified - locked session, disconnected Remote Desktop session, a desktop
switch, the duplication limit, an unduplicatable mode, a second instance, a device fault - and reported through
`StatusDto.UnavailableReason` / `State = "no-output"`, so the dashboard and the tray icon say "not recording"
instead of showing green. The synthetic source now requires an explicit `--synthetic-test` and says so when it is
used.

### 4.1 The adaptive cadence could not slow the loop down when the compositor is chatty

**Where:** `AdaptiveCadence.NextTimeout` is the only throttle the loop has, and its output is the *acquire
timeout*; `CaptureEngine.Run` hands it to `_source.TryAcquire`.

**Evidence:** on the live Release recorder, `ThrottleLevel` was pinned at its maximum (4) — the self-throttle had
decided per-frame cost was over budget — while `AverageAcquireMs` was `0.00 ms` and `AverageProcessMs` 19.3 ms.
The throttle widened the cadence and the pace did not change, because `AcquireNextFrame` returned a frame
immediately on every iteration: 6,201 frames in 1,779 s, every one of them with changes.

**Why it matters:** spec §5.1's backoff ("up to 1–2 s between checks") only bites when the compositor is *silent*.
Anything presenting continuously — a 60 Hz animation, video, a spinner, a remote-desktop session, an animated
cursor trail — makes every acquire instant, and then frames/second is bounded only by how fast the loop can
process them. The self-throttle's single lever is disconnected exactly when it is needed.

**Scale:** the smoke run's synthetic source (62.5 fps by construction) cost 18.3% of the machine. A loop paced to
1 frame/s would cost ~0.3%. Everything else in this section is smaller than this one.

**If it bites:** decide a *minimum interval between processed frames* inside the cadence and honour it with a real
sleep, keeping the zero-timeout burst behaviour only while the dirty-rect area is large or the interval is already
met.

**Fixed:** `AdaptiveCadence.NextDelay` is now a duty cycle against the measured frame cost - the loop sleeps after a
frame that had changes, for long enough that `frameMs / share` frames per second keeps the frame path inside its
share of the machine (1% of the box, half the spec's 2%, leaving the writer and GC the rest). It is bounded at 250 ms
so a pathological frame cannot stop recording altogether, and the self-throttle level now widens *this* instead of
the acquire timeout that did nothing. `PaceIntervalMs` and `FramesPaced` are in the status payload, so the governor
can be watched instead of trusted.

### 4.2 The per-frame readback is a full-surface GPU copy and map, not just the dirty rects

**Where:** `DesktopDuplicator.TryAcquireFrame` — `CopyResource` of the whole desktop texture, then
`_context.Flush()`, then `Map`, then `BufferMapper.CopyToPacked` copying the entire staging buffer.

**Evidence:** 0.0–0.3 ms per frame on the development machine (1366×768), so it is *not* the present cause — but
`LastReadbackMs = 58 ms` was observed once on the live recorder during a duplication rebuild, a 16× outlier
against the 6.2 ms in `docs/VALIDATION.md` §3. The cost scales with the surface: 4.2 MB per frame at 1366×768,
33 MB at 4K. At 62 fps that is ~260 MB/s today and ~2 GB/s at 4K.

**Why it matters:** this is the one place the capture thread can stall on the GPU, because `Map` on a staging
resource blocks until the copy has landed. Spec §5.1's "never process a full frame when only a corner changed"
holds for hashing and tiling but not for the readback that feeds them.

**If it bites:** `CopySubresourceRegion` per dirty-rect union instead of a whole-surface copy, or copy only the
rows the dirty rects span out of the already-mapped staging buffer. Fixing §4.1 caps this by construction.

**Fixed, on the CPU side:** the readback is now region-based. The rect list is read *before* the pixels, expanded
outward to tile boundaries (`ReadbackRegions`, mirroring `TileCellSet.AddRect` so the pixels read back always cover
the tiles about to be hashed), and only those rows are copied out of the mapped surface
(`BufferMapper.CopyRegionsToPacked`). Anything uncertain - a pending full rescan, a DPI-scaled surface, a rect
outside the surface, more than half the screen dirty - falls back to the whole-surface copy rather than risking a
tile assembled from two frames. The engine tells the backend when it needs every pixel through
`IFrameSource.FullFrameRequired`.

The GPU-side `CopyResource` stays a single whole-surface blit on purpose: it is one driver call instead of one per
region, and GPU bandwidth is the budget the spec explicitly allows to spend (spec 3) while CPU is the budget it does
not. That is a deliberate limit of this fix, not an oversight.

### 4.3 The capture thread drains the whole asset queue every two seconds

**Where:** `CaptureEngine.Maintenance` → `FlushSessionState` → `DrainWriterUnbounded` (unbounded
`Thread.Sleep(1)` spin), and `WriteCheckpoint` → `_assetWriter.Drain()`.

**Evidence:** with ~105 new tiles/s the queue sat at 691–809 payloads pending, against a measured store ceiling
of ~197 files/s on this machine (`docs/VALIDATION.md` §2 — ~5 ms per stored tile with real-time AV scanning on).
`AverageProcessMs` stayed at 2.8–3.2 ms and `AverageAcquireMs` at ~0.2 ms throughout, so the phase counters do
not cover this wait at all.

**Why it matters:** the drain is deliberate — the log must never become durable before the tiles it references —
so the flag is "the drain is *unbounded* and called from the hot loop", not "the ordering rule is wrong". It is
also why a live session's log lags under heavy change (documented in `docs/ROADMAP.md`).

**If it bites:** drain to a watermark, or only when the queue has been quiet for N ms, instead of emptying it on
every pass. Crash-consistency survives that: it comes from draining *before* the flush, not from draining
*completely*.

**Fixed:** both halves. `AssetWriteQueue` signals an event when it empties and `Drain` blocks on it instead of
polling a counter with `Thread.Sleep(1)`; and the loop's routine two-second flush now defers when more than
`DrainHighWater` (512) payloads are still queued, leaving the log buffered rather than stalling the capture thread
for seconds (`FlushSessionState(deferIfBusy: true)`). Control paths - pause, purge, prune, day rollover, root
switch, shutdown - never defer, because they are about to act on what the log says.

### 4.4 Most of the process's CPU is not inside the per-frame instrumentation

**Evidence:** live recorder — 6,201 frames / 1,779 s = 3.5 fps × 19.3 ms of processing accounts for 6.8% of one
core, while the process averaged 64% of one core (16% of the machine; 1,145.8 s of CPU over 1,779 s of wall).
Smoke run — 62.6 fps × ~3 ms accounts for ~4.7% of the machine against 18.3% measured.

**Why it matters:** the `readback / hash / encode / store / log` counters are useful for *relative* attribution
but they do not add up to the process total; the missing mass is the writer thread, GC, and anything else off the
capture thread. Treating that breakdown as a complete budget will mislead — **answering "which function costs the
most" needs a profiler (`dotnet-trace`), not more counters.** The harness now produces both the number to profile
against and the conditions to profile under (a static screen and a scripted active desktop).

**Addressed, not closed:** the gap is now measurable from the status payload - loop pace and paced-frame count,
dedupe cache hit rate, queue depth, and the source block - so the next round of work can tell *which* of the
off-thread costs moved.

**Closed by the profiler, with a twist (§5a).** The trace was run and the missing mass is now attributed: on the
capture thread the three largest costs are the per-tile **phase instrumentation itself** (23.0%), the **drain wait**
(21.1%) and **memcpy** (14.8%), against tiles encoding at 2.3%. The twist is that the instrumentation this section
was written to *complement* is the single largest thing on the thread — so the `readback / hash / encode / store /
log` breakdown is not just incomplete, it is partly self-measurement, and a per-tile stopwatch cadence fix is owed
before those counters are trusted again.

### 4.5 One heap allocation per tile hash, and the hash is 1.8× slower than it needs to be

**Where:** `storage-lib/TileHash.cs` — `Compute` creates `new XxHash3()` on every call.

**Evidence** (`.dev-logs/hashprobe`, Release): `XxHash3.IsValueType` is `False`, so each call allocates.
**568 bytes per 64×64 tile**, and 3.45 µs/tile versus **1.89 µs/tile and 0 bytes** for the same hash with one
reused instance plus `Reset()`. The live recorder hashed 592,618 tiles in 30 minutes → ~337 MB of gen0 garbage
(≈11 MB/min); one 4K full-surface rescan allocates 1.1 MB on its own.

**Ranking, so this is not oversold:** it is real, it is cheap to fix, and it is *not* the dominant cost — the
saving is under a second per 30 minutes of hashing at that tile rate. It matters because the garbage it creates
is charged against a 2% budget through GC, and because `docs/ROADMAP.md` states as a rule that the hot path has
no per-frame allocations. This is the one place that rule is currently broken.

**If it bites:** a thread-static `XxHash3` reused with `Reset()` (the API's documented pattern), or hashing the
four dimension bytes into a stack prefix and calling the static one-shot hash. Re-measure with the same probe.

**Fixed:** `TileHash` now keeps one hasher per thread and resets it per tile - zero allocations per hash, and the
1.8x is banked. `TileGridTests.HashIsStableAcrossReuse` guards the thing that could go wrong instead: state leaking
between tiles would make identical content hash differently, which the dedupe layer would read as new content
forever.

### 4.6 The dedupe cache forgets everything at once, and a miss becomes a filesystem probe on the capture thread

**Where:** `DedupeCache.Add` clears the whole set when it reaches 262,144 entries; `CaptureEngine.ProcessTiles`
then escapes to `_session.Assets.Contains(hash)` — a file-existence probe — on the **capture thread** for every
hash the cache missed.

**Evidence:** not hit in any measurement so far (34k tiles stored in 30 minutes, well under the cap), so this is a
latent risk rather than a present cost. On a full day of use the cap *will* be reached, and each time it is, the
following rescan pays one filesystem probe per tile.

**If it bites:** size the cache against the store instead of hard-coding a count, evict a segment rather than
everything, and keep existence probes on the writer thread where the I/O already lives.

**Fixed (the cliff):** `DedupeCache` holds two generations and rotates the older one out when the newer fills, so a
hash survives two full rotations and the memory footprint is unchanged (the capacity is split between them). The
remaining part of the risk - that a genuine miss still probes the filesystem on the capture thread - is now
*visible*: `DedupeCacheHits`/`Misses`/`Entries` are in the status payload and the hit rate is printed by
`--cpu-bench` and shown in the dashboard, so a cache that stops working shows up as a number rather than as CPU.

### 4.7 Two store walks run from the hot loop

**Where:** `CaptureEngine.Maintenance` calls `AssetStats()` and `SessionBytes()` on every iteration; each fires a
`Task.Run` walk of the asset store or the session folder once 30 s have passed.

**Evidence:** not isolated — the walks are off-thread and single-flight, so they do not appear in the phase
counters. Flagged because on a store with tens of thousands of files a directory walk is real CPU inside the
process total, and the 30 s interval is fixed regardless of store size.

**If it bites:** let the refresh interval grow with the store size, or drop the loop-driven warm-up and refresh
only when a consumer actually asks.

### 4.8 Playback rendered stripes: two geometries for one monitor id, resolved by file order

**Symptom:** replay of a real session rendered repeating stripes — a synthetic desktop's content laid out on the
real desktop's grid (and the reverse), with content bleeding across the whole frame.

**Not the cause (both checked and ruled out, as asked):**

- **`ReadbackRegions` / tile-boundary snapping.** `ReadbackRegionsTests` pins the invariant directly: every cell
  `TileCellSet.AddRect` produces for a set of rects is contained in a readback region for the same rects. A stride
  or off-by-one error there would show up as tiles hashed as new on nearly every frame — the 5+5 run measured 42.5%
  of hashed tiles *identical to the canvas*, which cannot happen if readback were copying the wrong rows. The
  profiler trace agrees: `BufferMapper.CopyToPacked` is 12 leaf samples on the capture thread, not a hotspot.
- **The two-generation dedupe cache.** The cache only ever answers "is this hash stored?". It never resolves a hash
  to a tile, so it cannot return the wrong asset. Rotation is content-preserving: a hash survives two rotations,
  and a miss falls through to `AssetStore.Contains` — a false miss costs a probe, never a wrong payload. The
  generation-rotation change has since added `DedupeCacheTests` covering the wrap and the two-generation lifetime.

**The cause:** `meta.json` for the live session held **two rows for monitor id 0** — the real display
(`\\.\DISPLAY1`, 1366×768, grid 22×12) and the fallback (`synthetic`, 1024×768, grid 16×12) — because DXGI was
refusing the locked desktop for two minutes and the recorder that was running then wrote under the same id. Two rows
for one id mean two tile grids, and tile (x, y) is different pixels under each:

```
\\.\DISPLAY1 1366 first=11:37:50 last=13:49:39
synthetic   1024 first=12:15:47 last=12:17:13
```

`SessionMeta.AllMonitors()` picked the newest row per id and the replayer seeded the canvas with it; every entry from
the other geometry window was then indexed on the wrong grid. A 1024-wide grid laid out on a 1366-wide canvas puts
column 15 back at pixel 0, which draws as exactly the vertical stripes in the report.

**Fix (player + meta only; no capture change):**

1. `SessionMeta.MonitorsAt(ms)` / `GeometryAt(id, ms)` / `MonitorAt(id, ms)` — the geometry **in force at a
   timestamp**. Inside a row's own first/last-seen window that row wins; the tie between a re-sighted row (whose
   window widens across a foreign window) and the foreign row is broken by the most recently *changed* row, i.e. the
   containing window with the latest first-seen.
2. `SessionReplayer.SeekTo` applies that geometry *after* loading a checkpoint, so a checkpoint written by the other
   recorder cannot impose its grid on the replay.
3. `SessionReplayer.SyncGeometry` resolves the row **per entry**, not once per window. The window cache it replaced
   was the bug in miniature: a re-sighted row's widened window (11:37→13:49) contains the foreign window
   (12:15→12:17), so the cached check short-circuited and never switched grids. `SetMonitor` is a no-op when the
   geometry is unchanged, so the per-entry call is cheap.
4. `ScreenCanvas.SetMonitor` drops tile state when the grid dimension changes (it already did) and preserves it when
   only the origin moves — so switching grids mid-replay resets to holes and rebuilds from the log, which is correct.

**Regression tests:** `PlaybackTests.ReplayUsesTheGeometryInForceWhenEachEntryWasRecorded` builds a session with a
wide row, a foreign narrow row and a wide row again, and asserts `Canvas.Monitor(0).Columns` is 4 → 2 → 4 with the
right tile hash at each step. `SessionMetaTests` covers the resolution rules (`GeometryIsResolvedPerTimestamp`,
`MonitorsAtReportsOneEntryPerId`, `TheGeometryWindowTravelsWithTheRow`,
`TheNewestGeometryWinsRatherThanTheLastRowInTheFile`).

**Verified visually, not just by unit test** — `player-cli render` on the real session:

| Position | Correct render |
|---|---|
| `+2275` (12:15:46, last moment of the real display before the foreign window) | 1366×768, the real desktop |
| `+2290` (12:16:01, inside the foreign window) | **1024×768**, the synthetic desktop drawn on its own grid |

Both were reconstructed from the live `dev-data/live-mode` session with the fallback's pixel content intact and
nothing bleeding outside its own width. The stripe artifact is gone.

**Residual risk:** a meta file whose rows for one id are *both* re-sighted across each other's windows is
ambiguous by construction; the "latest first-seen wins inside a covering window" rule is the one that matches
capture behaviour (the recorder re-sights its display every 30 s, and the geometry in force is the one it just
measured). The `MaxMonitorEntries = 64` cap and the foreign-source scenario deserve a test if a third source ever
records under one id.

## 5. Measurements so far

### The first real benchmark: Release, real desktop, 5 + 5 minutes

`Trace.cmd bench 5` on the development machine (1366×768, 4 logical cores, DXGI duplication working, session
unlocked), logged to `.dev-logs/cpu-bench-2026-09-22-133402.log`:

```text
cpu-bench result: idle=13.136% active=11.445% target=2.000% idle-presents=173 dedupe=75.8% verdict=INVALID
```

| Phase | Average | Peak | Presents | Tiles hashed | Compression skipped |
|---|---|---|---|---|---|
| static screen | 13.136% | 31.892% | 173 | — | — |
| active use | 11.445% | 31.892% | 712 | 41,083 | 75.8% |

**Read that honestly: the target is not met.** Both phases sit ~6x over the 2% budget, and the verdict is INVALID
rather than PASS because the idle phase was not static - the desktop presented 173 times in five minutes with the
operator's hands off (this machine's terminal UI animates, and the benchmarking agent itself polls every 30 s), so
`screen was static` failed as designed. The idle figure is real but it describes a lightly-busy desktop, not an idle
one.

What the same run does confirm about the fixes:

- **The governor engaged.** `pace 250 ms between frames (712 frames deliberately waited after)`, `throttle level 4`,
  `cadence frame cost 515.0 ms`. The loop is now paced by measured cost instead of running at whatever rate the
  compositor offered - 712 frames in 305 s (2.3 fps) instead of the ~1,100 frames a minute the old loop processed.
- **The readback is region-based and correct.** 42.5% of hashed tiles were recognised as pixel-identical to the
  canvas and a further 33.3% as known hashes on disk: if the partial readback were copying the wrong rows, tiles
  would have hashed as new nearly every frame and those two numbers would be near zero.
- **Dedupe still skips compression** for 75.8% of hashed tiles (target 80%) - just under, on a workload that
  invented 9,943 genuinely new tiles (24.2%) in five minutes.
- **Nothing was substituted.** The run took the DXGI path end to end; `capturable output: ok`.

What the numbers now point at, in order (**and what the profiler then confirmed**, §5a):

1. **Per-frame cost is much larger than its parts.** `process 515.0 ms average` and `encode 264.9 ms` in the last
   frame, against a hash cost of ~3 ms - the phase counters do not account for it. The trace now says most of it is
   the per-tile phase instrumentation itself plus filesystem work, and that `QoiCodec.EncodeInto` is only 2.3% of
   capture-thread samples: the `encode 264.9 ms` figure was a single frame's counter, not a rate. See §5a.
2. **The dedupe cache hit rate was only 57.9%**, against 9,943 filesystem probes on the capture thread — a direct
   consequence of 24% of hashed tiles being genuinely new content, plus a cache that starts empty against a store
   that already holds 370k assets. §5a ranks the probe path as the second-largest capture-thread cost.
3. **The machine was not idle.** 173 presents in the idle phase and 31.9% peaks in both phases include the
   benchmarking agent's own activity; a run on a genuinely quiet, unlocked machine is still owed.

| Run | Build | Source | Idle avg | Active avg | Verdict |
|---|---|---|---|---|---|
| **Short re-measure after the corruption fix, 2026-09-22 15:26, 20 s + 20 s** (`--activity none`) | Release | **DXGI, 1366×768, unlocked** | **11.994%** | **20.794%** (peak 46.4%) | **FAIL** — real desktop, real numbers, still ~6× over budget |
| Governor check, 2026-09-2x | Release | synthetic (`--synthetic-test`, ground-truth dumps on) | — | measured | pacing engaged: 419 frames / 33 s (~13 fps) under an artificial generator with per-frame ground-truth dumps on |
| Harness smoke, 2026-09-22, 30 s + 30 s (pre-fix) | Release | synthetic (DXGI unavailable) | 18.323% | 13.691% | **INVALID** — established what a continuously-presenting desktop costs |
| Build-gate check, 2026-09-22 | Debug | synthetic | 24.429% | 24.715% | **INVALID** — the build gate fired as designed |
| Live recorder, 2026-09-22, 30 min (pre-fix) | Release | DXGI, 1366×768 | — | 16.1% (64% of one core) | no verdict; counters only |

The first row is the newest and the only short-run number taken on the real desktop after the fixes of §4.1-4.7 and
§4.8. Read its phase blocks carefully: the idle phase measured **5 presents in 20 s with 1,320 tiles hashed** — the
`screen was static` gate passed (≤10 presents) but 5 full rescans still happened, 462 of those tiles were genuinely
new content, and the dedupe cache answered only 9.1% of lookups from memory against a store holding 370k assets.
That run is where §5a's trace was taken. Neither phase is a PASS and neither is a clean idle measurement.

Read the synthetic and Debug rows carefully; **neither is a real-desktop budget measurement.**

- The **smoke run is a ceiling, not a forecast**: DXGI duplication was unavailable in that session
  (`E_ACCESSDENIED` — a locked or secure desktop), so the harness fell back to the synthetic source, which
  generates frames on a 16 ms timer as fast as it can. It establishes that the harness measures, that its gates
  fire, and roughly what a continuously-presenting desktop costs. Its "screen was static" gate correctly failed.
- The **Debug run exists to prove the gate**, not to compare builds. Note that it was only ~1.3× the Release
  figure on the same synthetic workload — most of this cost is not JIT-sensitive, which is exactly why "it's just
  a Debug build" cannot be assumed to explain a CPU number.
- The **live recorder** figure comes from `TotalProcessorTime` over its uptime, before the harness existed.

**The 5 + 5 baseline on a real desktop exists, but it is INVALID, and that matters.** The run of 2026-09-22 13:34
took the DXGI path on an unlocked desktop end to end (`capturable output: ok`) and still measured `idle=13.136%`,
because the desktop presented 173 times in five minutes with hands off. A static-screen budget cannot be certified
from a screen that was not static, so the verdict is INVALID rather than FAIL — see the table above. What is still
missing is a 5 + 5 run on a *genuinely quiet* machine; `Trace.cmd bench 5` is the command and §3 is the checklist it
feeds. Until that exists, the honest statement is: the target is met nowhere, the cause is ranked in §4 and §5a, and
the fixes those sections name have not been applied yet.

## 5a. Profiler data, and the GPU question

### The trace

`dotnet-trace collect --providers Microsoft-DotNetCore-SampleProfiler` for 30 s against a Release recorder taking
the **real** DXGI desktop path (`--once`, 1366×768, session unlocked), converted with
`convert --format speedscope` and attributed per thread. The capture thread is the profile containing
`CaptureEngine.Run`; 513 samples landed on it. `.dev-logs/once-live.nettrace`.

**Top leaf functions on the capture thread** (exclusive samples, so this is where the thread actually stood):

| # | Function | Share | What it is |
|---|---|---|---|
| 1 | `Stopwatch.GetElapsedTime` + `Stopwatch.GetTimestamp` | **23.0%** | the per-tile phase instrumentation in `ProcessTiles` |
| 2 | `ManualResetEventSlim.Wait` + `SpinWait.SpinOnceCore` | **21.1%** | the capture thread waiting on the asset writer (`AssetWriteQueue.Drain`) |
| 3 | `Buffer.Memmove` + `Buffer._Memmove` | **14.8%** | memcpy: `Marshal.Copy` readback and `CopyTile` |
| 4 | `PathHelper.Normalize` | **8.0%** | path handling for `File.Exists` / `PathFor` — the dedupe probe |
| 5 | `FileSystem.FillAttributeInfo` | **4.7%** | the same `File.Exists` probe, hitting the filesystem |
| 6 | `Thread.Sleep` | 4.5% | the cadence sleep between frames |
| 7 | `CaptureEngine.ProcessTiles` (self) | 3.9% | loop body |
| 8 | `QoiCodec.EncodeInto` | **2.3%** | **the tile encoder** |
| 9 | `HashSet<ulong>.AddIfNotPresent` | 1.4% | `TileCellSet` / `DedupeCache` |
| 10 | `CopyTile` + `SessionLogWriter.Append` | 1.2% each | tile copy and log append |

Process-wide `dotnet-trace report topN` says the same thing from the other direction: after the pure waits
(`LowLevelLifoSemaphore`, `Monitor.Wait`, `IOCompletionPoller`, `ManualResetEventSlim`), the largest real costs are
the store's **file I/O path** — `CreateRelativeDirectoryHandle` 3.08%, `AssetStore.Store` 2.08%, `Kernel32.WriteFile`
1.67%, `Kernel32.CreateFilePrivate` 1.19%, `CloseHandle` 0.87% — with `TileHash.Compute` at 0.58%.

### 1. Is the encoder CPU-only? Yes — and it is *not* the blocker

The codec is `QoiCodec`, pure managed C# in `storage-lib` (`TileCodec.cs` → `QoiCodec.Encode`), no native library,
no GPU. There is no hidden hardware path.

But the earlier `encode 264.9 ms` figure was a **single frame's stopwatch counter**, and the profiler disagrees with
it: **`QoiCodec.EncodeInto` is 2.3% of capture-thread samples.** That stopwatch line was partly measuring the act of
measuring — see point 1 below.

Measured on this machine (`.dev-logs/codecprobe.log`, 64×64 BGRA tile, Release):

| Codec | µs/tile | MB/s input | Output | Allocated |
|---|---|---|---|---|
| `qoi-lossless` (the capture default) | **114.73** | 136 | 20.0 KB | 41,040 B/tile |
| `qoi-rgb565` (balanced) | 143.54 | 109 | 20.0 KB | 57,448 B/tile |
| `deflate-qoi` (archive) | **1,383.74** | 11 | 18.0 KB | 80,288 B/tile |
| `TileHash.Compute` (incompressible data, worst case) | 2.76 | 5,662 | — | 0 B |

A full-surface rescan therefore costs **30 ms of encode at 1366×768** (264 tiles) and **234 ms at 3840×2160**
(2,040 tiles). The 5+5 run saw 0.25 full rescans/s, i.e. ~8 ms/s of encoder work against a budget of 80 ms/s
(2% of a 4-core machine = 8% of one core). **Encoding cannot be the reason CPU sits at 12–13%.**

Two real findings fall out of the table anyway: the archive codec is **12× slower than the capture default** and
must never become the capture codec (it is documented as archive-only, and this is the number that says why); and
QOI allocates **41 KB per tile** because `QoiCodec.Encode` allocates an upper-bound buffer (`MaxEncodedSize` =
20,502 B) and then a right-sized copy. At a 4K full rescan that is ~84 MB of gen0 garbage per frame. Worth an
`EncodeInto`-with-reused-buffer call site, and it is a CPU fix, not a GPU one.

### 2. Should compression or hashing move to the GPU? No — measured against evidence

- **Hashing is already effectively free.** `TileHash.Compute` is 0.58% of samples and 2.76 µs/tile: a full 4K rescan
  hashes in 5.6 ms. Moving 0.58% of CPU to the GPU cannot move a 12% number.
- **QOI does not map to a compute shader.** Its per-tile state machine — previous pixel, run-length counter, 64-entry
  index, and the byte-vs-`0xFE`/`0xFF` opcode choice — is strictly sequential with data-dependent control flow. A GPU
  port means one thread per tile running a serial loop (no parallelism inside a tile) or a warp-cooperative
  ballot/shuffle scheme like the GPU ports of LZ/ANS codecs: research-grade work for a codec that measures 2.3%.
  Even a perfect port moves ~2% of the capture thread and adds a readback of the compressed stream plus
  command-buffer overhead per frame, against a workload that is one 30 ms job per second at most.
- **The GPU is already busy with the part that suits it.** `CopyResource` (whole-surface GPU blit) and the DXGI
  duplication surface are GPU work; only the region *marshal* is CPU (`BufferMapper`, 12 leaf samples). Spec §3's
  5–20% GPU band is spent on the copy, which is the correct division.

**What the profile says to fix instead**, in order of measured size:

1. **The instrumentation is the largest single capture-thread cost (23%).** Four `Stopwatch.GetElapsedTime`-based
   phase accumulators per tile exist to publish `LastHashMs`/`LastEncodeMs`/`LastStoreMs`/`LastLogMs`. At 264 tiles
   that is over a thousand timer reads per frame. Keep the counters, but sample the phase timers every Nth frame, or
   gate them behind the benchmark switch. This is a measurable regression **introduced by the observability work**
   and it must be fixed before further budget claims are read off those counters.
2. **Per-tile filesystem work on the capture thread (≈13%).** `PathHelper.Normalize` + `FillAttributeInfo` +
   `CreateRelativeDirectoryHandle` + `CreateFilePrivate` are the existence probe (`_session.Assets.Contains(hash)`)
   and the per-tile `FileStream` construction in `AssetStore.Store`. The dedupe cache is meant to absorb the probes,
   and its hit rate against a *warm store* is 9–36% because it starts empty and only learns hashes it happens to see
   (§4.6). **Seeding it from the asset manifest at session start is the highest-value fix now on the table** — it
   removes a disk probe per already-stored tile from the capture thread.
3. **The drain wait (21%).** This is the capture thread blocked on a store at its ~200 files/s ceiling (§4.3).
   Cheaper writes — fewer, larger, batched — shrink it.

### 3. GPU adapters present, and how a dedicated GPU would be preferred

`--probe` enumerates DXGI adapters with the two facts a compute decision needs (this machine):

```text
adapter #0    : Intel(R) HD Graphics 520 (hardware, dedicated 128 MB, shared 8125 MB)
adapter #1    : Microsoft Basic Render Driver (software, dedicated 0 MB, shared 8125 MB)
```

There is **no dedicated GPU in this machine** — only an Intel iGPU — so the "fall back to integrated" case is the
only case that exists here, and it works: duplication, the blit and the region readback all run on adapter #0.

Preference is structural rather than coded: `DXGI.EnumAdapters1` returns hardware adapters before the software/WARP
adapters (confirmed by the ordering above), and `DesktopDuplicator.TryCreate` creates its D3D11 device on **the
adapter that owns the output being duplicated** (`target.Adapter`) — which is exactly the right GPU. A dedicated GPU
would be picked up automatically when present, with no policy code. Should a compute path ever be added, that is the
rule to keep: bind to the adapter of the output being captured, and treat the iGPU as first-class rather than
requiring a discrete card.

**Decision: no GPU offload is implemented.** The profiling does not support it, and spec §3's GPU band is already
spent on the blit. The three fixes above are CPU-side and measured.

## 5b. The three profiled costs: what was fixed, and what the numbers actually did

### Fix 1 — instrumentation (was 23% of the capture thread)

`RecallConfig.DetailedTiming`, **off by default**, copied by `Clone()` (it was not — see below), toggled by
`--detailed-timing` for the bench. With it off, `ProcessTiles` costs two timestamp reads per *frame* and publishes
`LastTileLoopMs`; with it on, the four per-tile phases are measured as before. The bench prints which mode produced
its number instead of silently mixing them.

**Profiler, after:** `Stopwatch.GetElapsedTime` / `GetTimestamp` are **0 samples** on the capture thread (was 23.0%).
A fresh 30 s trace of a production-mode recorder, 175 samples on that thread: `ProcessTiles` 6.9%,
`SessionLogWriter.Append` 4.6%, `PathHelper.Normalize` 4.0%, `DesktopDuplicator.TryAcquireFrame` 3.4%,
`Regex.RunSingleMatch` 2.3%, `XxHashShared.Append` 2.3%, `Buffer.Memmove` 2.3%.
**Fix verified by profiler.**

### Fix 2 — cold dedupe cache

`AssetManifest.ReadHashes` streams the session's manifests (today's parts, then up to seven previous days, newest
first) into `DedupeCache.AddIfNew` before the first frame. **262,144 hashes seeded** on the live store, against a
cache that held 749 on the cold runs before. `DedupeCache.AddIfNew` caps at `Capacity` — the first version spilt
into the older generation without a cap and half the seed was discarded on the next rotation.

**What the counters say:** on a run where the desktop content was mostly already-stored, the hit rate was **92.9%**
(was 9.1%). On runs of 87-93% genuinely-new content it is 0.7-31%, which is the workload, not the cache: a cache can
only answer for hashes it has seen, and the manifests cannot contain tiles the user had never displayed before.
7 filesystem probes vs 1,424 on the comparable cold run.

### Fix 3 — drain wait

`ManualResetEventSlim` **spin-waits before blocking**, and `WaitForEmpty` re-entered it in a loop, so every re-check
paid the spin again. Replaced with a kernel `ManualResetEvent.WaitOne`. Slice kept at 250 ms on purpose: the defect
was the spin, not the interval, and a 2 s slice was measured to make the pixel-perfect fidelity run drop to 44%
because a control-path drain blocked too long on a loaded machine.

**Profiler, after:** `ManualResetEventSlim` + `SpinWait` are **0 samples** (was 21.1%).

### Things that were tried and reverted

- **Removing the `lock` in `SessionLogWriter.Append`** (it was 4.6% of samples): **reverted.** The fidelity test
  dropped to 79% immediately — `Flush()` is reachable from IPC threads, so a drain can catch a half-filled buffer.
  The lock is load-bearing. Mentioned because the profile makes this look like free CPU, and it is not.
- **`FileOptions.WriteThrough` on the asset temp file:** **reverted.** It makes each 20 KB tile write synchronous
  and unbuffered, the opposite of what the store needs.
- **`StoreBatch`:** written, measured, **not kept** — it did not change the per-tile cost, which is file *creation*
  and rename, not the bytes.

### A real bug found on the way

`RecallConfig.Clone()` was a hand-written property copy that **never copied `MaxPaceMs`** — the governor's ceiling
was reset to its default on every clone, so a user-configured pacing was silently discarded. It would have dropped
`DetailedTiming` too. `RecallConfigTests.CloneCopiesEveryProperty` now reflects over the type so the next property
added cannot be lost the same way.

### The numbers, and why they are not a result

| Run (30 s + 30 s unless noted) | Mode | Store | Idle | Active | Idle presents | New content |
|---|---|---|---|---|---|---|
| pre-fix, 20 s + 20 s | production | cold temp | 11.994% | 20.794% | 5 | 35% |
| A2 | detailed timing ON | warm | 23.802% | 20.493% | 19 | 86% |
| B3 | production | warm | 23.231% | 15.540% | 29 | 90% |
| A3 | detailed timing ON | warm | 22.463% | 17.080% | 22 | 87% |
| B5 | production | warm | 28.128% | 20.530% | 14 | 93% |

**No fix can be credited with a CPU reduction here, and saying otherwise would be dishonest.** The spread between
two runs of the *same* configuration class (22.5% vs 28.1%) is wider than any fix's effect, because every run
measured a *changing* desktop: 14-59 presents per phase with 86-93% of hashed tiles being brand-new content. The
`screen was static` gate fired on all five runs. The instrumented/uninstrumented delta — the cleanest comparison
available, since only the flag differs — is within noise.

### The actual blocker, now measured

`.dev-logs/storeprobe.log`, writing real tiles to a fresh store:

```text
store 1 tile      : 9.30 ms/tile (20.0 KB in)
264-tile rescan   : 2.5 s of writer-thread work when every tile is new
cost at N new/s   : N x 9.3 ms/s of writer CPU = N x 0.93% of a 4-core box
```

At the rate these workloads actually produced new tiles (~47/s), that is **~10.6% of a four-core machine from the
asset writer alone**, plus the capture thread blocked in `Drain()` waiting for it — which is the 20-28% measured.
The cost is not the bytes (20 KB into a 64 KB buffer); it is **one file per tile**: create, write, flush, close,
rename, on a volume with real-time scanning.

**This is a storage-format problem, not a tuning problem.** Getting near 2% requires the asset store to stop being
one file per tile. That is what §5c does.

## 5c. The packed store: many tiles per file, and the number it produced

Spec §5.3 permits either packing or a container volume; the store now packs. `AssetPackStore` appends records to
`assets/packs/NNNNNN.pack` (64 MB rolls) and commits each tile by appending a 32-byte record to `NNNNNN.idx`. The
commit point is the index record, not the pack bytes: a record is written (and the pack flushed) before the log entry
that references the tile can become durable, so a crash can never leave a log entry naming a tile no reader can
reach. Startup `LoadIndex` discards a torn tail — a bad magic or a record whose extent runs past the end of the pack —
and trims the pack back to its last committed offset, so an interrupted append costs one tile, never the pack.

Reclaiming is a pure file delete, with **no compaction**: a pack with even one live tile is kept whole until its last
tile expires. That trades disk space for a reclaim path that cannot race a reader and needs no rewrite protocol.

Same probe, same 20 KB tiles, packed store:

```text
store 1 tile      : 0.22 ms/tile (20.0 KB in)          <- was 9.30 ms/tile
264-tile rescan   : 0.1 s of writer-thread work when every tile is new   <- was 2.5 s
cost at N new/s   : N x 0.2 ms/s of writer CPU = N x 0.02% of a 4-core box   <- was N x 0.93%
```

**~42× less writer-thread work per new tile.** At the 47 new tiles/s these workloads actually produced, the writer
goes from ~10.6% of a four-core box to ~0.25% — and, because the capture thread's `Drain()` wait is that same work
moved off its back, the 20-28% in §5b's table is where this fix is expected to show up rather than in a new
instrumentation number.

The refactor made one thing explicit that the per-instance version had wrong: the capture writer and the retention
merge walk are **separate `AssetStore` instances over the same root**, and each had its own in-memory index — so the
walk could not see tiles the writer had just written, and the writer could not see tiles the walk had deleted. The
index, the per-pack live counts and the open pack handles are now process-wide state keyed by the packs directory,
under one gate. Two consequences worth naming:

- `Delete` is durable again: it rewrites the owning pack's `.idx` without the deleted record. For the pack a writer
  is appending to, that rewrite goes **through the writer's own handle** (truncate, rewrite survivors, appended at the
  end) rather than an atomic temp-and-move replace — replacing a file under a live writer is exactly the sharing
  violation that made the earlier design give up and go in-memory-only.
- If that rewrite cannot land, `Delete` still reports the tile as deleted, because in this process it is; the stale
  record is re-deleted by the next walk. A crash mid-rewrite tears the index tail, which `LoadIndex` already handles:
  records before the tear load, and the worst case is one tile resurrecting — never a torn middle hiding good tiles.

Evidence: full suite green after the change — 86 tests, including `RetentionTests` (the referenced-vs-orphan merge
walk over a packed store), `AssetStoreTests`, `LongRunHousekeepingTests`, `PipelineTests` and the legacy `.tile` read
path. Legacy per-file tiles are still readable, so an existing store keeps working while its tiles age out.

### Still owed

- **A genuinely idle desktop.** Every number in §5b has 14-59 presents in its "idle" phase. Nothing there is a valid
  idle measurement and the 2% target is neither met nor disproven. Run `Trace.cmd bench 5` with no agent, no
  terminal output and nothing else animating; the harness already refuses to certify above 10 presents. The packed
  store's ~10 percentage points of expected savings are a *probe result and a forecast*, not yet a bench result —
  the bench is what turns them into one.
- **QOI's 41 KB of garbage per tile** (§5a), and a look at what `Regex.RunSingleMatch` (2.3%) is doing on the
  capture thread now that the two big costs above it are gone.

## 6. What this does not cover

- **GPU counters.** GPU time is not measured, only reasoned about: DXGI duplication, the whole-surface
  `CopyResource` blit and the region marshal all touch the GPU, and §5a explains why the encoder is not worth moving
  there. The spec's 5–20% GPU band is therefore spent rather than quantified — a GPU counter source (PDH
  `GPU Engine` counters, or ETW `Microsoft-Windows-DxgKrnl`) is what would close that gap.
- **The capture-thread profile is one 30 s trace, ~500 samples.** It is decisive about the *ranking* (23% vs 2.3% is
  not noise) but it is not a P95 and it was taken while `--once` was doing repeated full rescans, which is at least
  the right workload shape for the question asked.
- **Multi-monitor.** Every measurement here is single-display. A second display doubles both the readback and the
  present rate at the same frame cadence (§4.2), and the shared static rect buffers in `FrameRectsReader` deserve
  a second look on that path.
- **P95 and the 8-hour run.** `--cpu-bench` reports an average and the widest sample. Spec §12 asks for P95 over
  8+ hours, which is `--soak`'s job (it samples the same CPU figure per interval), not this harness's.
- **Anything about the dashboard.** It is a separate process; only the recorder is measured here.
