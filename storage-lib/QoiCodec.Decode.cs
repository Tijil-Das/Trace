namespace ScreenRecall.Storage;

public static partial class QoiCodec
{
    /// <summary>Reads the pixel dimensions out of a QOI stream header.</summary>
    public static void ReadHeader(ReadOnlySpan<byte> encoded, out int width, out int height)
    {
        if (encoded.Length < 14 || encoded[0] != 'q' || encoded[1] != 'o' || encoded[2] != 'i' || encoded[3] != 'f')
        {
            throw new InvalidDataException("Not a QOI stream (bad magic).");
        }

        width = (int)ReadUInt32(encoded, 4);
        height = (int)ReadUInt32(encoded, 8);
        if (width <= 0 || height <= 0 || (long)width * height > 4096 * 4096)
        {
            throw new InvalidDataException($"Invalid QOI dimensions {width}x{height}.");
        }
    }

    /// <summary>Decodes a QOI stream into a newly allocated BGRA buffer.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> encoded, out int width, out int height)
    {
        ReadHeader(encoded, out width, out height);
        byte[] pixels = new byte[width * height * 4];
        DecodeInto(encoded, width, height, pixels);
        return pixels;
    }

    /// <summary>Decodes a QOI stream into a caller-provided BGRA buffer.</summary>
    public static void DecodeInto(ReadOnlySpan<byte> encoded, int width, int height, byte[] destination)
    {
        if (destination.Length < width * height * 4)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        Span<byte> index = stackalloc byte[64 * 4];
        byte r = 0, g = 0, b = 0, a = 255;
        int p = 14;
        int total = width * height;

        for (int i = 0; i < total; i++)
        {
            if (p >= encoded.Length)
            {
                throw new InvalidDataException("Truncated QOI stream.");
            }

            byte b1 = encoded[p++];
            if (b1 == 0xFE)
            {
                EnsureAvailable(encoded, p, 3);
                r = encoded[p++];
                g = encoded[p++];
                b = encoded[p++];
            }
            else if (b1 == 0xFF)
            {
                EnsureAvailable(encoded, p, 4);
                r = encoded[p++];
                g = encoded[p++];
                b = encoded[p++];
                a = encoded[p++];
            }
            else
            {
                switch (b1 & 0xC0)
                {
                    case 0x00:
                    {
                        int io = (b1 & 0x3F) * 4;
                        r = index[io];
                        g = index[io + 1];
                        b = index[io + 2];
                        a = index[io + 3];
                        break;
                    }

                    case 0x40:
                        r = (byte)(r + ((b1 >> 4) & 0x03) - 2);
                        g = (byte)(g + ((b1 >> 2) & 0x03) - 2);
                        b = (byte)(b + (b1 & 0x03) - 2);
                        break;

                    case 0x80:
                    {
                        EnsureAvailable(encoded, p, 1);
                        byte b2 = encoded[p++];
                        int dg = (b1 & 0x3F) - 32;
                        r = (byte)(r + dg + ((b2 >> 4) & 0x0F) - 8);
                        g = (byte)(g + dg);
                        b = (byte)(b + dg + (b2 & 0x0F) - 8);
                        break;
                    }

                    default:
                    {
                        int run = (b1 & 0x3F) + 1;
                        for (int k = 0; k < run && i < total; k++, i++)
                        {
                            int dst = i * 4;
                            destination[dst] = b;
                            destination[dst + 1] = g;
                            destination[dst + 2] = r;
                            destination[dst + 3] = a;
                        }

                        i--;
                        continue;
                    }
                }
            }

            int hashIndex = ((r * 3) + (g * 5) + (b * 7) + (a * 11)) % 64;
            int io2 = hashIndex * 4;
            index[io2] = r;
            index[io2 + 1] = g;
            index[io2 + 2] = b;
            index[io2 + 3] = a;

            int offset = i * 4;
            destination[offset] = b;
            destination[offset + 1] = g;
            destination[offset + 2] = r;
            destination[offset + 3] = a;
        }
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> encoded, int position, int count)
    {
        if (position + count > encoded.Length)
        {
            throw new InvalidDataException("Truncated QOI stream.");
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
        => ((uint)buffer[offset] << 24)
           | ((uint)buffer[offset + 1] << 16)
           | ((uint)buffer[offset + 2] << 8)
           | buffer[offset + 3];
}
