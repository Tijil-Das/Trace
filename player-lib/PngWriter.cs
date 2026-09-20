using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>
/// Minimal PNG encoder (lossless, 8-bit RGBA). Written in-house rather than pulling an imaging
/// dependency: the player only needs to write frames out for inspection and for the fidelity harness,
/// and one dependency fewer keeps the install small.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes tightly packed BGRA pixels as a PNG file.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid frame size {width}x{height}.");
        }

        int expected = width * height * 4;
        if (bgra.Length < expected)
        {
            throw new ArgumentException($"Expected {expected} pixel bytes, got {bgra.Length}.", nameof(bgra));
        }

        byte[] raw = new byte[((width * 4) + 1) * height];
        int rawPosition = 0;
        for (int y = 0; y < height; y++)
        {
            raw[rawPosition++] = 0; // filter: None (fast; zlib still compresses UI content well)
            for (int x = 0; x < width; x++)
            {
                int source = ((y * width) + x) * 4;
                raw[rawPosition++] = bgra[source + 2];
                raw[rawPosition++] = bgra[source + 1];
                raw[rawPosition++] = bgra[source];
                raw[rawPosition++] = bgra[source + 3];
            }
        }

        byte[] compressed;
        using (MemoryStream output = new())
        {
            using (System.IO.Compression.ZLibStream zlib = new(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = output.ToArray();
        }

        using MemoryStream png = new();
        png.Write(Signature);
        using (MemoryStream header = new())
        {
            WriteBigEndian(header, width);
            WriteBigEndian(header, height);
            header.WriteByte(8); // bit depth
            header.WriteByte(6); // colour type: RGBA
            header.WriteByte(0); // deflate
            header.WriteByte(0); // adaptive filtering
            header.WriteByte(0); // no interlace
            WriteChunk(png, "IHDR", header.ToArray());
        }

        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>Writes a frame to disk atomically.</summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> bgra)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".part";
        File.WriteAllBytes(temp, Encode(width, height, bgra));
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteChunk(Stream stream, string type, byte[] payload)
    {
        Span<byte> length = stackalloc byte[4];
        length[0] = (byte)(payload.Length >> 24);
        length[1] = (byte)(payload.Length >> 16);
        length[2] = (byte)(payload.Length >> 8);
        length[3] = (byte)payload.Length;
        stream.Write(length);

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(payload);

        uint crc = Crc32(typeBytes, payload);
        Span<byte> crcBytes = stackalloc byte[4];
        crcBytes[0] = (byte)(crc >> 24);
        crcBytes[1] = (byte)(crc >> 16);
        crcBytes[2] = (byte)(crc >> 8);
        crcBytes[3] = (byte)crc;
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in first)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte value in second)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
