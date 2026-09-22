using System.Buffers;
using System.IO.Hashing;

namespace ScreenRecall.Storage;

/// <summary>
/// Content hashing for tiles (spec 5.2). xxHash3 (non-cryptographic, very fast) over the raw tile
/// pixels plus its dimensions, so a partial edge tile can never collide with a full tile that
/// happens to share its pixel prefix. Hash value 0 is reserved as the "no asset" sentinel.
/// </summary>
public static class TileHash
{
    /// <summary>Sentinel meaning "this tile holds nothing" in checkpoints and canvases.</summary>
    public const ulong None = 0;

    /// <summary>
    /// Own-instance hasher: <c>System.IO.Hashing.XxHash3</c> is a reference type and is documented as not
    /// thread-safe, so a shared/reused instance across the capture thread, the player and any pooled-thread flow
    /// that hashes stacked slices is undefined behaviour. The thread-static reuse was tried and reverted when the
    /// pixel-perfect fidelity test went non-deterministically to 0% in isolation: own-instance hashing is the
    /// conservative rule until a microbenchmark plus five fidelity runs prove a reuse pattern safe.
    /// </summary>
    [ThreadStatic]
    private static XxHash3? _threadHasher;

    // Reserved for that future experiment. Do not use without the proof above.
    private static XxHash3 Hasher => _threadHasher ??= new XxHash3();

    /// <summary>Hashes a tightly packed BGRA tile of the given pixel dimensions.</summary>
    public static ulong Compute(ReadOnlySpan<byte> bgra, int width, int height)
    {
        // NOT shared state: XxHash3 is documented by System.IO.Hashing as NOT thread-safe, and Reset()+reuse
        // across threads is undefined even with ThreadStatic if any async or pooled-thread flow shares it.
        // The allocation (a few dozen bytes) is real but small; the comment makes the non-choice explicit so no
        // one "fixes" it back into a shared field on a hot path.
        XxHash3 hash = new();
        Span<byte> dims = stackalloc byte[4];
        dims[0] = (byte)(width & 0xFF);
        dims[1] = (byte)((width >> 8) & 0xFF);
        dims[2] = (byte)(height & 0xFF);
        dims[3] = (byte)((height >> 8) & 0xFF);
        hash.Append(dims);
        hash.Append(bgra);
        return ToNonZero(hash.GetCurrentHashAsUInt64());
    }

    /// <summary>Hashes a sub-rectangle of a strided BGRA buffer (no copy for the common full-tile case).</summary>
    public static ulong ComputeRegion(
        ReadOnlySpan<byte> buffer,
        int stride,
        int x,
        int y,
        int width,
        int height)
    {
        int bytes = width * 4;
        if (stride == bytes)
        {
            return Compute(buffer.Slice((y * stride) + (x * 4), bytes * height), width, height);
        }

        // Stacked slices may call from contexts the thread-static was never meant to serve (fidelity harness,
        // player decoders, batched writers). Same rule as above: own instance, no sharing.
        XxHash3 hash = new();
        Span<byte> dims = stackalloc byte[4];
        dims[0] = (byte)(width & 0xFF);
        dims[1] = (byte)((width >> 8) & 0xFF);
        dims[2] = (byte)(height & 0xFF);
        dims[3] = (byte)((height >> 8) & 0xFF);
        hash.Append(dims);

        byte[] rented = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            for (int row = 0; row < height; row++)
            {
                buffer.Slice(((y + row) * stride) + (x * 4), bytes).CopyTo(rented);
                hash.Append(rented.AsSpan(0, bytes));
            }

            return ToNonZero(hash.GetCurrentHashAsUInt64());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Hex form of a hash as used in asset file names.</summary>
    public static string ToHex(ulong hash) => hash.ToString("x16");

    private static ulong ToNonZero(ulong value) => value == None ? 1 : value;
}
