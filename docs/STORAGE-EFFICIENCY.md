# Storage efficiency: where the bytes actually go

Written after measuring a real store on the development machine (`T:\Coding\Trace\dev-data`), because the
interesting question is not "is our codec good" but "which change moves the number".

## 1. Lossless densification — implemented (codec id 3, "archive")

The tile payload was the obvious first target, so it was measured rather than guessed. Benchmark: 2,000 real
stored tiles (shard/hash order, i.e. content-random) from a genuine evening desktop session, 64×64, encoded and
decoded in-process, decode checksums compared.

| Codec | Bytes | vs raw BGRA | Encode/tile | Decode/tile |
|---|---|---|---|---|
| raw BGRA (reference) | 32,499,200 | 1.00× | — | — |
| **QOI (previous default)** | **5,212,814** | **6.23×** | **107 µs** | **50 µs** |
| QOI → Deflate, SmallestSize | **4,074,131** | **7.98×** | 297 µs | 100 µs |
| QOI → Deflate, Optimal | 4,084,330 | 7.96× | 330 µs | 127 µs |
| QOI → Brotli q4 | 4,202,192 | 7.73× | 1,569 µs | 172 µs |
| QOI → Brotli q6 | 4,147,918 | 7.84× | 5,578 µs | 152 µs |
| QOI → Brotli q11 | 3,877,378 | 8.38× | 14,658 µs | 174 µs |

**Decision: QOI → Deflate (`DeflateQoiTileCodec`, id 3).** It is **21.8% denser than plain QOI** (5,212,814 →
4,074,131 bytes) and *lossless*: decoding runs Deflate then QOI and the pixel checksum matches QOI exactly, so
the fidelity harness result is unchanged. The reason it works is that QOI has already de-correlated the pixels
(runs for flat colour, small deltas for gradients, a 64-colour index for repetition); what is left is a stream of
opcode bytes and small literals with heavy skew, which is what Deflate's LZ window plus Huffman coding is good
at. Brotli q11 is only a further 4.8% denser while costing **49× more CPU per tile** and offering no decode
advantage — at 100+ changed tiles per second that is a latency budget the recorder cannot spend, and Deflate is
in-box in .NET, so no third-party codec enters a privacy tool.

Nothing here is lossy. `QuantizedQoiTileCodec` (5-6-5 colour, id 2) still exists as the "balanced" mode, but it
discards pixel precision, and compression of the record must never be paid for by the thing being recorded.

### Before / after, per hour of recording

Applying the measured 0.7816 size factor to the day-attributed tiles (see §3 for how those were attributed):

| Scenario | Before: tiles/h | log/h | **Before total** | After: tiles/h | **After total** | Change |
|---|---|---|---|---|---|---|
| Real desktop, 3.4 h evening session | 53.8 MB | 1.2 MB | **55.0 MB** | 42.1 MB | **43.3 MB** | **−21.4%** |
| Real desktop, 34 min session | 30.2 MB | 0.7 MB | **30.9 MB** | 23.6 MB | **24.3 MB** | −21.4% |
| Continuously-changing desktop | 404 MB | 4.7 MB | **408.7 MB** | 315.8 MB | **320.5 MB** | −21.6% |

An 8-hour day of the middle case drops from ~247 MB to ~194 MB of payload. These are payload bytes; §2 is what
turns payload into bytes *on disk*.

### How to turn it on

`FidelityMode: "archive"` in the config. `lossless` (plain QOI) stays the default because archive encoding costs
about 2.8× the CPU per changed tile, which matters on a busy desktop. When a day exhausts its daily budget the
engine now steps *down* the ladder archive → lossless → balanced, so an over-budget archive day falls back to
cheap-and-fast lossless rather than to a different kind of losslessness.

## What is already efficient

| Layer | Design | Effect |
|---|---|---|
| Dedupe | content-addressable tiles, one file per distinct 64×64 tile | an unchanged icon, menu bar or window frame is stored **once ever**, not once per frame |
| Codec | QOI, lossless, self-contained | ~8× smaller than raw BGRA on UI content (`TileCodec`) |
| Change detection | 64px grid, hashes only dirty tiles | a still desktop costs nothing at all |
| Log | fixed 27-byte records, append-only | 238k entries/s, 0.004 ms per append |

Measured on the same machine: **2,023 bytes average per stored tile** (`live` store, 40 shard-directory
sample). Raw would be 16,384 bytes. So the pixel path is already ~8× down, and dedupe is what keeps it from
being multiplied by the frame rate.

## Where the bytes actually go: filesystem slack

The store writes **one small file per distinct tile**. NTFS allocates whole clusters, and `T:` has 4,096-byte
clusters (`Get-CimInstance Win32_Volume -Filter "DriveLetter='T:'"` → `BlockSize = 4096`). A 2,023-byte tile
therefore occupies **4,096 bytes on disk** — half of every byte in the store is slack.

Measured (sample of 40 shard leaf directories, extrapolated):

| Store | Tiles (est.) | Logical | Allocated | Waste |
|---|---|---|---|---|
| `dev-data/live` | ~22,000 | 42.4 MB | 85.9 MB | **2.03×** |
| `dev-data/live-mode` | ~121,000 | 202.4 MB | ~404 MB | ~2.0× |

Tile size distribution in the sample: 4 tiles < 256 B, 4 in 256–512 B, 7 in 512 B–1 KB, 12 in 1–2 KB,
16 in 2–4 KB, 3 above 4 KB. The mass sits just under one cluster, which is the worst case for a
file-per-tile layout.

Reproduce it with:

```powershell
$assets = 'T:\Coding\Trace\dev-data\live\assets'
$leaf   = Get-ChildItem "$assets\*\*" -Directory
$sample = $leaf | Select-Object -First 40
$n = 0; [long]$sum = 0; [long]$alloc = 0
foreach ($d in $sample) {
  foreach ($f in [System.IO.Directory]::EnumerateFiles($d.FullName, '*.tile')) {
    $l = (New-Object System.IO.FileInfo $f).Length; $n++; $sum += $l
    $alloc += ([math]::Ceiling($l / 4096.0) * 4096)
  }
}
"avg bytes per tile : {0:N0}" -f ($sum / $n)
"allocated per tile : {0:N0}" -f ($alloc / $n)
```

## 3. Per-day attribution (the numbers used in §1)

A day's cost is not visible from the asset store: the store is content-addressed and shared by every day, so a
tile first written on Monday and re-used on Tuesday belongs to Monday (and is counted again if Tuesday's
manifest references it). The per-day manifest (`sessions/<day>/assets*.idx`, 8 bytes per hash) is what makes a
day's own cost answerable. For `dev-data/live-mode`:

| Day | Recorded span | Distinct tiles | Tile bytes | Log | Checkpoints | Total | Per hour |
|---|---|---|---|---|---|---|---|
| 2026-09-20 | 3 h 22 m | 103,171 | 180.7 MB | 3.89 MB | 0.06 MB | 184.7 MB | **55.0 MB/h** |
| 2026-09-21 | 34 m | 11,416 | 17.3 MB | 0.41 MB | 0.01 MB | 17.7 MB | **30.9 MB/h** |

## What is *not* the win

- **Compressing the log.** Measured log sizes are 0.41 MB, 0.77 MB and 3.89 MB per day, against 42–202 MB of
  tiles. Even a perfect 10× on the log saves a few megabytes; the tiles are the store.
- **A lossier tile codec as the default.** `QuantizedQoiTileCodec` (5-6-5 colour) shrinks payloads by discarding
  pixel precision. It exists as "balanced", and it is the wrong default: the frames *are* the reconstruction, and
  everything in §1 and §2 is recoverable with **zero** loss of fidelity.

## The fix that is still open: pack files

Reorganise the asset store from one file per tile to **append-only packs**:

```text
<root>/assets/ab/pack-000.pack        payload bytes, back to back, byte-identical to today's payloads
<root>/assets/ab/pack-000.idx         8 bytes per entry: uint64 hash, then offset+length (or a hash → offset map)
```

- **Lossless by construction.** Payload bytes are copied, not re-encoded. The 24-byte per-tile header
  disappears into the index, so this also removes ~24 bytes × tile count of framing.
- **Kills the slack.** A pack holds thousands of tiles, so the only partial cluster is at the end of the pack
  (a fraction of one cluster per pack, not per tile). Expected: ~2,023 bytes/tile allocated instead of 4,096.
- **Sequential reads.** Tiles referenced close together in time land close together in the pack, which is the
  access pattern playback already has.

What it costs, and why it is a decision rather than a patch:

1. **Retention GC changes shape.** Deleting a day can no longer unlink tile files; packs must be *compacted*
   (rewrite live entries into a new pack, delete the old one). `RetentionPruner.Gc` currently merges hash lists
   and deletes files — that logic becomes a compaction pass with a temporary space cost.
2. **Read path.** `AssetStore.TryLoadTile` gains an index lookup and a positioned read; the dedupe probe
   (`Contains`) becomes an index query instead of `File.Exists`, which is *faster*.
3. **Tests that pin the current layout must change.** `AssetStoreTests` asserts `PathFor(hash)` names a file and
   that enumeration is sorted by hash; `verify --content` walks that enumeration. Both are reasonable contracts
   to move, but they are contracts.
4. **Migration.** Existing stores would need a one-time repack, or the reader must support both layouts.

Recommendation: land it as a **new layout with a reader that supports both**, default new stores to packs, and
repack old ones in the background; do not attempt an in-place rewrite of a store that holds the only copy of
someone's screen history.

## The number to watch

`logical bytes ÷ allocated bytes` for the asset store, measured the same way as above. Today it is ~0.49
(2.03× waste). The point of packing is to make it ~0.95+. If a change does not move that ratio, it is not
addressing the actual cost of the store.

## Status and verification

- **§1 (denser lossless payload) is implemented** — codec id 3, selected with `FidelityMode: "archive"`. It does
  not move `logical / allocated`; it shrinks the logical side. That is why both numbers are tracked.
- **§2 (pack files) is not implemented** — it is the change that moves the allocation ratio to ~0.95+.

Verified:

- Codec bit-exactness is asserted in `CodecTests.ArchiveCodecIsLosslessAndDenserThanQoi`.
- Independently checked across 2,000 real stored tiles: QOI and QOI-then-Deflate produced identical decode
  checksums (746500), and the 0.7816 size factor was measured on those same real tiles, not on synthetic images.

Not yet run:

- The full spec §12 fidelity harness (`player-cli fidelity`) against a session captured in archive mode. The
  codec is bit-exact in isolation, but the end-to-end ground-truth comparison is the test that would catch a
  pipeline-level mistake, and it should be run before archive becomes anyone's default.
