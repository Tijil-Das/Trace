using System.Diagnostics;

using ScreenRecall.CaptureService.Cli;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService;

/// <summary>
/// <c>--bench [tiles]</c>: times the three hot-path primitives — tile hashing, QOI encoding and asset
/// store writes — on this machine. Keeps the performance conversation honest: when the capture loop
/// looks slow, this says which of the three is responsible.
/// </summary>
internal static class BenchCommand
{
    internal static int Run(int tileCount, string? rootOverride)
    {
        tileCount = Math.Clamp(tileCount <= 0 ? 512 : tileCount, 16, 200_000);
        string root = rootOverride ?? Path.Combine(Path.GetTempPath(), "screen-recall-bench", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        const int tileSize = 64;
        int length = tileSize * tileSize * 4;
        byte[][] tiles = new byte[tileCount][];
        Random random = new(1234);
        for (int i = 0; i < tileCount; i++)
        {
            byte[] tile = new byte[length];
            random.NextBytes(tile);

            // Half the tiles are flat UI-like content, half are noisy: the mix QOI sees in practice.
            if (i % 2 == 0)
            {
                for (int p = 0; p < length; p += 4)
                {
                    tile[p] = 0x30;
                    tile[p + 1] = 0x30;
                    tile[p + 2] = 0x40;
                    tile[p + 3] = 0xFF;
                }
            }

            tiles[i] = tile;
        }

        Console.WriteLine($"bench: {tileCount} tiles of {tileSize}x{tileSize}, root {root}");
        Console.WriteLine($"machine: {Environment.ProcessorCount} logical core(s)");

        ulong[] hashes = new ulong[tileCount];

        Stopwatch clock = Stopwatch.StartNew();
        for (int i = 0; i < tileCount; i++)
        {
            hashes[i] = TileHash.Compute(tiles[i], tileSize, tileSize);
        }

        double hashMs = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"hash    : {hashMs:0.0} ms total, {hashMs / tileCount * 1000:0.0} µs/tile, "
                          + $"{length * tileCount / 1024.0 / 1024.0 / (hashMs / 1000):0.0} MB/s");

        byte[][] payloads = new byte[tileCount][];
        clock.Restart();
        for (int i = 0; i < tileCount; i++)
        {
            payloads[i] = QuantizedQoiTileCodec.Instance.IsLossless == false
                ? QoiTileCodec.Instance.Encode(tiles[i], tileSize, tileSize)
                : QoiTileCodec.Instance.Encode(tiles[i], tileSize, tileSize);
        }

        double encodeMs = clock.Elapsed.TotalMilliseconds;
        long payloadBytes = payloads.Sum(p => (long)p.Length);
        Console.WriteLine($"qoi     : {encodeMs:0.0} ms total, {encodeMs / tileCount * 1000:0.0} µs/tile, "
                          + $"{payloadBytes / 1024.0 / 1024.0:0.00} MB out ({payloadBytes / (double)tileCount:0} B/tile)");

        DateTimeOffset day = DateTimeOffset.Now;
        DateOnly dayOnly = DateOnly.FromDateTime(day.LocalDateTime);
        SessionStore session = SessionStore.Open(root, dayOnly);
        clock.Restart();
        for (int i = 0; i < tileCount; i++)
        {
            session.Assets.Store(hashes[i], QoiTileCodec.Instance.Id, tileSize, tileSize, payloads[i]);
        }

        double storeMs = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"store   : {storeMs:0.0} ms total, {storeMs / tileCount * 1000:0.0} µs/tile, "
                          + $"{tileCount / (storeMs / 1000):0} files/s");

        clock.Restart();
        using (SessionLogWriter log = session.OpenLog(day))
        {
            for (int i = 0; i < tileCount; i++)
            {
                log.Append(LogEntry.Draw(day.ToUnixTimeMilliseconds() * 1000, 1, 0, i % 22, i / 22, hashes[i]));
            }
        }

        double logMs = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"log     : {logMs:0.0} ms total for {tileCount} entries, {tileCount / (logMs / 1000):0} entries/s");

        clock.Restart();
        int decoded = 0;
        for (int i = 0; i < tileCount; i++)
        {
            TileBitmap tile = session.Assets.TryLoadTile(hashes[i], verifyHash: true);
            decoded += tile.Width;
        }

        double loadMs = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"verify  : {loadMs:0.0} ms for {tileCount} tiles (decode + hash check), {tileCount / (loadMs / 1000):0} tiles/s, checksum {decoded}");

        double captureEquivalentTilesPerHour = 3600_000 / (hashMs / tileCount + encodeMs / tileCount + storeMs / tileCount);
        Console.WriteLine();
        Console.WriteLine($"cold-start equivalent: {captureEquivalentTilesPerHour:0} tiles/hour at this rate");
        Console.WriteLine($"total                 : {hashMs + encodeMs + storeMs + logMs:0.0} ms for {tileCount} tiles");
        return 0;
    }
}
