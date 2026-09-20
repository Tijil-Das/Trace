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

    /// <summary>Hashes a tightly packed BGRA tile of the given pixel dimensions.</summary>
    public static ulong Compute(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var hash = new XxHash3();
        Span<byte> dims = stackalloc byte[4];
        dims[0] = (byte)(width & 0xFF);
        dims[1] = (byte)((width >> 8) & 0xFF);
        dims[2] = (byte)(height & 0xFF);
        dims[3] = (byte)((height >> 8) & 0xFF);
        hash.Append(dims);
        hash.Append(bgra);
        ulong value = hash.GetCurrentHashAsUInt64();
        return value == None ? 1 : value;
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

        var hash = new XxHash3();
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
