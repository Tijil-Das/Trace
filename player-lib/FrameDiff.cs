namespace ScreenRecall.Player;

/// <summary>Result of comparing two same-sized BGRA frames.</summary>
public sealed record FrameDiffResult(
    long TotalPixels,
    long MismatchedPixels,
    byte MaxChannelDelta,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY)
{
    /// <summary>Fraction of pixels that differ beyond the tolerance.</summary>
    public double MismatchRatio => TotalPixels == 0 ? 0 : (double)MismatchedPixels / TotalPixels;

    /// <summary>Percentage form, for reports.</summary>
    public double MismatchPercent => MismatchRatio * 100.0;

    /// <summary>True when every pixel matched.</summary>
    public bool IsExact => MismatchedPixels == 0;

    /// <summary>Pixel match ratio (the number the spec's fidelity target is expressed in).</summary>
    public double MatchRatio => 1.0 - MismatchRatio;

    /// <summary>Bounding box of the differing pixels, or null when the frames match.</summary>
    public (int X, int Y, int Width, int Height)? DifferenceBounds
        => IsExact ? null : (MinX, MinY, MaxX - MinX + 1, MaxY - MinY + 1);
}

/// <summary>
/// Pixel comparison used by the fidelity harness (spec 12). Counts differing pixels, tracks the worst
/// channel delta and the bounding box of the difference, so a failure says where and how badly the
/// reconstruction drifted rather than just that it did.
/// </summary>
public static class FrameDiff
{
    /// <summary>Compares two BGRA frames of the same size.</summary>
    public static unsafe FrameDiffResult Compare(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual,
        int width,
        int height,
        int tolerance = 0)
    {
        int stride = width * 4;
        long required = (long)stride * height;
        if (expected.Length < required || actual.Length < required)
        {
            throw new ArgumentException($"Frames must hold at least {required} bytes for {width}x{height}.");
        }

        int rowsPerPartition = Math.Max(1, height / Math.Max(1, Environment.ProcessorCount * 4));
        int partitionCount = Math.Max(1, (height + rowsPerPartition - 1) / rowsPerPartition);
        long[] mismatches = new long[partitionCount];
        int[] worstDelta = new int[partitionCount];
        int[] minX = new int[partitionCount];
        int[] minY = new int[partitionCount];
        int[] maxX = new int[partitionCount];
        int[] maxY = new int[partitionCount];

        // Spans cannot cross into a lambda, so the pixel buffers are pinned and only their addresses
        // are captured: the diff then runs in parallel without copying either frame.
        fixed (byte* expectedPin = expected)
        fixed (byte* actualPin = actual)
        {
            byte* expectedBase = expectedPin;
            byte* actualBase = actualPin;

            Parallel.For(0, partitionCount, partition =>
            {
                int startRow = partition * rowsPerPartition;
                int endRow = Math.Min(height, startRow + rowsPerPartition);
                long localMismatch = 0;
                int localWorst = 0;
                int localMinX = int.MaxValue;
                int localMinY = int.MaxValue;
                int localMaxX = -1;
                int localMaxY = -1;

                for (int y = startRow; y < endRow; y++)
                {
                    int rowOffset = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int offset = rowOffset + (x * 4);
                        int delta = 0;
                        for (int channel = 0; channel < 4; channel++)
                        {
                            int difference = Math.Abs(expectedBase[offset + channel] - actualBase[offset + channel]);
                            if (difference > delta)
                            {
                                delta = difference;
                            }
                        }

                        if (delta <= tolerance)
                        {
                            continue;
                        }

                        localMismatch++;
                        if (delta > localWorst)
                        {
                            localWorst = delta;
                        }

                        if (x < localMinX)
                        {
                            localMinX = x;
                        }

                        if (x > localMaxX)
                        {
                            localMaxX = x;
                        }

                        if (y < localMinY)
                        {
                            localMinY = y;
                        }

                        if (y > localMaxY)
                        {
                            localMaxY = y;
                        }
                    }
                }

                mismatches[partition] = localMismatch;
                worstDelta[partition] = localWorst;
                minX[partition] = localMinX;
                minY[partition] = localMinY;
                maxX[partition] = localMaxX;
                maxY[partition] = localMaxY;
            });
        }

        long totalMismatch = 0;
        int worst = 0;
        int globalMinX = int.MaxValue;
        int globalMinY = int.MaxValue;
        int globalMaxX = -1;
        int globalMaxY = -1;
        for (int i = 0; i < partitionCount; i++)
        {
            totalMismatch += mismatches[i];
            worst = Math.Max(worst, worstDelta[i]);
            if (maxX[i] >= 0)
            {
                globalMinX = Math.Min(globalMinX, minX[i]);
                globalMinY = Math.Min(globalMinY, minY[i]);
                globalMaxX = Math.Max(globalMaxX, maxX[i]);
                globalMaxY = Math.Max(globalMaxY, maxY[i]);
            }
        }

        if (globalMaxX < 0)
        {
            globalMinX = globalMinY = 0;
            globalMaxX = globalMaxY = -1;
        }

        return new FrameDiffResult(
            (long)width * height,
            totalMismatch,
            (byte)Math.Clamp(worst, 0, 255),
            Math.Max(globalMinX, 0),
            Math.Max(globalMinY, 0),
            globalMaxX,
            globalMaxY);
    }
}
