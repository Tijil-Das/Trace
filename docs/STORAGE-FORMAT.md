# Storage format

Everything here is little-endian. Fixed-width records are intentional: the reference log is append-only and
must be readable by any tool without a decoder, and a reader must be able to consume a prefix of a file
that is still being written.

```text
<root>/
  .screenrecall-lock                 exclusive lock held by the recording process
  index.db                           SQLite (WAL): day and window boundaries only
  assets/ab/cd/abcd...eff.tile      content-addressable tile store, sharded by hash prefix
  assets/tmp/*.part                  in-flight writes (ignore/clean these)
  sessions/YYYY-MM-DD/
    log.bin                          reference log (log.0001.bin after a restart)
    assets.idx                       hashes referenced that day (retention GC)
    meta.json                        monitor geometry observed that day
    checkpoints/<timestamp_us>.ckpt  full-state snapshots
    groundtruth/<timestamp_us>-<monitor>.raw   test-only fidelity dumps
```

`YYYY-MM-DD` is the **local** day, so day boundaries match what the user experienced; timestamps inside
files are unix microseconds (UTC).

## 1. Reference log — `log.bin`

Header, 32 bytes:

| Offset | Type | Meaning |
|---|---|---|
| 0 | char[6] | `SRLOG1` |
| 6 | u8 | version (1) |
| 7 | u8 | reserved |
| 8 | u64 | day start, unix milliseconds (local midnight) |
| 16 | u32 | flags |
| 20 | u32 | header size (32) |
| 24 | u32 | entry size (27) |
| 28 | u32 | reserved |

Then a stream of fixed **27-byte** records:

| Offset | Type | Meaning |
|---|---|---|
| 0 | u64 | timestamp_us (unix microseconds, UTC) |
| 8 | u32 | window id (stable hash of process + title) |
| 12 | u16 | monitor id (0-based, enumeration order) |
| 14 | u16 | tile x — **absolute** grid column, signed 16-bit bit pattern (monitors left of the primary are negative) |
| 16 | u16 | tile y — absolute grid row |
| 18 | u64 | asset hash (0 = cleared/empty) |
| 26 | u8 | op: 0 = DRAW, 1 = MOVE, 2 = CLEAR |

Notes:

- A tile **move** is a `CLEAR` of the source plus a `DRAW` of the destination; both regions are re-read from
  the frame, so replay stays one uniform loop.
- A reader stops at the last complete record, which is what makes live scrubbing safe.
- `CLEAR` is emitted when a monitor disappears; ordinary screen changes are draws of newly visible content.
- An all-zero record is never valid (timestamps are non-zero). The writer refuses to persist one and counts
  it as an integrity fault.

## 2. Tile asset — `assets/ab/cd/<16 hex digits>.tile`

| Offset | Type | Meaning |
|---|---|---|
| 0 | char[5] | `SRAS1` |
| 5 | u8 | codec id: 1 = QOI lossless, 2 = QOI after RGB565 quantization ("balanced") |
| 6 | u16 | tile width in pixels |
| 8 | u16 | tile height in pixels |
| 10 | u16 | flags (reserved) |
| 12 | u64 | content hash (must equal the file name) |
| 20 | u32 | payload length |
| 24 | … | codec payload (QOI stream) |

The hash is xxHash3 (64-bit) over `u16 width, u16 height,` followed by the tile's BGRA bytes. Dimensions are
part of the hash on purpose so a 22×64 edge tile can never collide with a full 64×64 tile sharing its pixel
prefix. Hash 0 is reserved as "no asset" and remapped to 1 if it ever occurs.

Writes go to `assets/tmp/<guid>.part` and are then renamed into place, so a crash cannot leave a half-written
asset that a log entry already references. `player-cli verify --content` decodes and re-hashes tiles, so both
truncation and silent corruption are detectable.

## 3. Checkpoint — `checkpoints/<timestamp_us>.ckpt`

| Offset | Type | Meaning |
|---|---|---|
| 0 | char[6] | `SRCKP1` |
| 6 | u8 | version (1) |
| 7 | u8 | flags (bit 0: sparse tile entries) |
| 8 | u64 | timestamp_us |
| 16 | u32 | monitor count |
| 20 | u32 | reserved |
| 24 | … | monitor blocks |

Each monitor block is a **32-byte** header followed by 12 bytes per non-empty tile:

| Offset | Type | Meaning |
|---|---|---|
| 0 | u16 | monitor id |
| 2 | u16 | tile size |
| 4 | i32 | x (virtual desktop) |
| 8 | i32 | y |
| 12 | u32 | width |
| 16 | u32 | height |
| 20 | u32 | columns |
| 24 | u32 | rows |
| 28 | u32 | entry count |
| 32 | u32 | tile index (row-major, dense) |
| 36 | u64 | asset hash |

Only non-empty cells are stored, so a mostly static screen costs a few hundred bytes. Checkpoints are written
through a paired `BinaryWriter`/`BinaryReader`; an earlier hand-rolled layout disagreed with its own size
constant, which is exactly the bug class the paired API prevents. Device names live in `meta.json`, not here.

## 4. Asset manifest — `assets.idx` (`assets.1.idx`, …)

| Offset | Type | Meaning |
|---|---|---|
| 0 | char[6] | `SRMAN1` |
| 6 | u8 | version (1) |
| 7 | u8 | flags |
| 8 | … | u64 hashes, deduplicated in memory before writing |

This is what makes retention GC cheap: instead of re-hashing every log entry in the retention window, the
pruner streams these files (plus checkpoint contents) into a scratch SQLite table and merge-walks them against
the hash-sorted asset listing.

## 5. Session meta — `meta.json`

```json
{
  "version": 1,
  "monitors": [
    { "id": 0, "deviceName": "\\\\.\\DISPLAY1", "x": 0, "y": 0,
      "width": 1366, "height": 768, "tileSize": 64,
      "firstSeenUnixMs": 1789912712000, "lastSeenUnixMs": 1789912712000 }
  ]
}
```

Geometry is recorded per day (and per change) because resolution changes and monitor hot-plug happen
mid-session. If the file is missing, the player falls back to the geometry embedded in the newest checkpoint.

## 6. Ground-truth frame — `groundtruth/<timestamp_us>-<monitor>.raw` (test only)

| Offset | Type | Meaning |
|---|---|---|
| 0 | char[5] | `SRGT1` |
| 5 | u8 | version (1) |
| 6 | u16 | monitor id |
| 8 | i32 | x |
| 12 | i32 | y |
| 16 | u32 | width |
| 20 | u32 | height |
| 24 | u32 | pixel byte length (width × height × 4) |
| 28 | … | BGRA rows, tightly packed |

Written at most once per second while `captureGroundTruth` is enabled, purely so the fidelity harness can
pixel-diff reconstruction against truth.

## 7. SQLite index — `index.db`

```sql
CREATE TABLE sessions     (day TEXT PRIMARY KEY, start_ts INTEGER, end_ts INTEGER, log_path TEXT);
CREATE TABLE window_spans (id INTEGER PRIMARY KEY AUTOINCREMENT, day TEXT, app_name TEXT,
                           window_title TEXT, start_ts INTEGER, end_ts INTEGER);
CREATE TABLE checkpoints  (day TEXT, ts INTEGER, path TEXT);
```

Boundaries only: which days exist, when a window had focus, where the checkpoints are. `window_spans` rows are
coalesced, so a long focus period is one row — navigation metadata, never usage analytics.

## Versioning rules

- Every file starts with a magic string and a version byte; unknown versions are rejected loudly instead of
  being guessed at.
- The log header records the entry size, so a future record change is detectable without inspecting the
  stream.
- The tile store is content-addressed and the log is append-only, so a format change is a *migration* rather
  than a rewrite: new sessions can use a new version while old ones stay readable.

