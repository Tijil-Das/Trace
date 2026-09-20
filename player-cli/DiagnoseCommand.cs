using ScreenRecall.Player;
using ScreenRecall.Storage;

namespace ScreenRecall.PlayerCli;

/// <summary>
/// <c>diagnose &lt;root&gt; &lt;day&gt; --at &lt;t&gt; [--monitor N] [--cell X,Y]</c>: walks one grid cell through
/// the whole pipeline — last log entry, canvas hash, stored asset, decoded pixel, and the ground-truth
/// pixel for the same instant — so a reconstruction discrepancy can be attributed to a specific stage
/// instead of guessed at.
/// </summary>
internal static class DiagnoseCommand
{
    internal static int Run(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));
        ushort monitor = ArgParser.Monitor(ArgParser.Option(args, "--monitor")) ?? 0;

        (int cellX, int cellY) = (0, 0);
        string? cellOption = ArgParser.Option(args, "--cell");
        if (cellOption is not null)
        {
            string[] parts = cellOption.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int parsedX) && int.TryParse(parts[1], out int parsedY))
            {
                cellX = parsedX;
                cellY = parsedY;
            }
        }

        using SessionReplayer replayer = new(root, day);
        long timestamp = ArgParser.Timestamp(
            ArgParser.Option(args, "--at") ?? "+0",
            day,
            replayer.FirstTimestampUs);

        Console.WriteLine($"day {day:yyyy-MM-dd}  target {ArgParser.FormatTimestamp(timestamp)} (us={timestamp})");

        // 1. The last log entry for this cell at or before the target.
        LogEntry? lastEntry = null;
        long entryCount = 0;
        long drawCount = 0;
        foreach (string segment in replayer.Store.LogSegments())
        {
            using SessionLogReader reader = new(segment);
            while (reader.TryReadNext(out LogEntry entry))
            {
                entryCount++;
                if (entry.MonitorId != monitor || entry.TileX != cellX || entry.TileY != cellY)
                {
                    continue;
                }

                drawCount++;
                if (entry.TimestampUs <= timestamp)
                {
                    lastEntry = entry;
                }
            }
        }

        Console.WriteLine($"log entries          : {entryCount} total, {drawCount} for cell ({cellX},{cellY})");
        Console.WriteLine(lastEntry is null
            ? "last entry <= target : NONE"
            : $"last entry <= target : op={lastEntry.Value.Op} hash={TileHash.ToHex(lastEntry.Value.AssetHash)} at {ArgParser.FormatTimestamp(lastEntry.Value.TimestampUs)}");

        // 2. Canvas state at the target.
        replayer.SeekTo(timestamp);
        ulong canvasHash = replayer.Canvas.TileAt(monitor, cellX, cellY);
        Console.WriteLine($"canvas hash          : {(canvasHash == TileHash.None ? "none (renders as a hole)" : TileHash.ToHex(canvasHash))} "
                          + $"(checkpoint {replayer.LastCheckpointUs?.ToString() ?? "none"}, {replayer.EntriesApplied} entries replayed)");

        // 3. The stored asset.
        if (canvasHash != TileHash.None)
        {
            bool exists = replayer.Store.Assets.Contains(canvasHash);
            Console.WriteLine($"asset present        : {exists}");
            if (exists)
            {
                try
                {
                    TileBitmap tile = replayer.Store.Assets.TryLoadTile(canvasHash, verifyHash: true);
                    Console.WriteLine($"asset decodes        : {tile.Width}x{tile.Height}, first pixel " + DescribePixel(tile.Bgra, 0));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    Console.WriteLine($"asset decode failed  : {ex.Message}");
                }
            }
        }

        // 4. The rendered frame and the ground truth at the same instant.
        RenderedFrame rendered = replayer.Render(monitor);
        MonitorInfo? geometry = replayer.Canvas.Monitor(monitor);
        if (geometry is not null)
        {
            geometry.TilePixelRect(cellX, cellY, out int x, out int y, out int width, out int height);
            Console.WriteLine($"render pixel ({x},{y})   : {DescribePixel(rendered.Bgra, ((y * rendered.Stride) + (x * 4)))}");
        }

        string? groundTruth = GroundTruthFrame.List(replayer.Store.SessionDir)
            .FirstOrDefault(file => file.EndsWith($"-{monitor}.raw", StringComparison.Ordinal)
                                    && GroundTruthFrame.TryParseTimestamp(file, out long ts)
                                    && ts == timestamp);
        if (groundTruth is not null)
        {
            GroundTruthImage image = GroundTruthFrame.Read(groundTruth);
            Console.WriteLine($"ground-truth pixel   : {DescribePixel(image.Bgra, 0)} ({image.Width}x{image.Height})");
        }
        else
        {
            Console.WriteLine("ground-truth frame   : none at this exact timestamp");
        }

        return 0;
    }

    private static string DescribePixel(byte[] bgra, int offset)
        => $"B={bgra[offset]} G={bgra[offset + 1]} R={bgra[offset + 2]} A={bgra[offset + 3]}";
}
