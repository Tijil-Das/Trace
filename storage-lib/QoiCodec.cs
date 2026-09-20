namespace ScreenRecall.Storage;

/// <summary>
/// QOI (Quite OK Image) lossless codec — spec 5.4's recommended fast-to-encode codec for flat UI
/// content. Self-contained: no native dependency, deterministic output, decodes pixel-exact.
/// Buffers are BGRA (Windows native order); the QOI stream itself carries RGBA.
/// </summary>
public static partial class QoiCodec
{
    /// <summary>Codec identifier stored in asset headers.</summary>
    public const byte CodecId = 1;

    /// <summary>Upper bound on the encoded size for a given pixel count.</summary>
    public static int MaxEncodedSize(int pixelCount) => (pixelCount * 5) + 14 + 8;

    /// <summary>Encodes tightly packed BGRA pixels into a QOI stream.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than width * height * 4.", nameof(bgra));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid tile dimensions {width}x{height}.");
        }

        byte[] output = new byte[MaxEncodedSize(width * height)];
        int length = EncodeInto(bgra, width, height, output);
        if (length == output.Length)
        {
            return output;
        }

        byte[] exact = new byte[length];
        Array.Copy(output, exact, length);
        return exact;
    }

    /// <summary>Encodes into a caller-provided buffer; returns the number of bytes written.</summary>
    public static int EncodeInto(ReadOnlySpan<byte> bgra, int width, int height, byte[] destination)
    {
        if (destination.Length < MaxEncodedSize(width * height))
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        int p = 0;
        destination[p++] = (byte)'q';
        destination[p++] = (byte)'o';
        destination[p++] = (byte)'i';
        destination[p++] = (byte)'f';
        WriteUInt32(destination, ref p, (uint)width);
        WriteUInt32(destination, ref p, (uint)height);
        destination[p++] = 4; // channels: RGBA
        destination[p++] = 0; // colorspace: sRGB + linear alpha

        Span<byte> index = stackalloc byte[64 * 4];
        byte prevR = 0, prevG = 0, prevB = 0, prevA = 255;
        int run = 0;
        int pixels = width * height;

        for (int i = 0; i < pixels; i++)
        {
            int src = i * 4;
            byte b = bgra[src];
            byte g = bgra[src + 1];
            byte r = bgra[src + 2];
            byte a = bgra[src + 3];

            if (r == prevR && g == prevG && b == prevB && a == prevA)
            {
                run++;
                if (run == 62 || i == pixels - 1)
                {
                    destination[p++] = (byte)(0xC0 | (run - 1));
                    run = 0;
                }
            }
            else
            {
                if (run > 0)
                {
                    destination[p++] = (byte)(0xC0 | (run - 1));
                    run = 0;
                }

                int hashIndex = ((r * 3) + (g * 5) + (b * 7) + (a * 11)) % 64;
                int io = hashIndex * 4;
                if (index[io] == r && index[io + 1] == g && index[io + 2] == b && index[io + 3] == a)
                {
                    destination[p++] = (byte)hashIndex;
                }
                else
                {
                    index[io] = r;
                    index[io + 1] = g;
                    index[io + 2] = b;
                    index[io + 3] = a;

                    if (a == prevA)
                    {
                        int dr = r - prevR;
                        int dg = g - prevG;
                        int db = b - prevB;
                        if (dr is >= -2 and <= 1 && dg is >= -2 and <= 1 && db is >= -2 and <= 1)
                        {
                            destination[p++] = (byte)(0x40 | ((dr + 2) << 4) | ((dg + 2) << 2) | (db + 2));
                        }
                        else
                        {
                            int drDg = dr - dg;
                            int dbDg = db - dg;
                            if (dg is >= -32 and <= 31 && drDg is >= -8 and <= 7 && dbDg is >= -8 and <= 7)
                            {
                                destination[p++] = (byte)(0x80 | (dg + 32));
                                destination[p++] = (byte)(((drDg + 8) << 4) | (dbDg + 8));
                            }
                            else
                            {
                                destination[p++] = 0xFE;
                                destination[p++] = r;
                                destination[p++] = g;
                                destination[p++] = b;
                            }
                        }
                    }
                    else
                    {
                        destination[p++] = 0xFF;
                        destination[p++] = r;
                        destination[p++] = g;
                        destination[p++] = b;
                        destination[p++] = a;
                    }
                }
            }

            prevR = r;
            prevG = g;
            prevB = b;
            prevA = a;
        }

        for (int i = 0; i < 7; i++)
        {
            destination[p++] = 0;
        }

        destination[p++] = 1;
        return p;
    }

    private static void WriteUInt32(byte[] buffer, ref int position, uint value)
    {
        buffer[position++] = (byte)(value >> 24);
        buffer[position++] = (byte)(value >> 16);
        buffer[position++] = (byte)(value >> 8);
        buffer[position++] = (byte)value;
    }
}
