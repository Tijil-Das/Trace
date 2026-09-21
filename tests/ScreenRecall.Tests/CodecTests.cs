using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>Tile codecs must be bit-exact: lossless is the product's default fidelity promise.</summary>
public sealed class CodecTests
{
    [Fact]
    public void QoiIsBitExactForUiLikeContent()
    {
        const int size = 64;
        byte[] bgra = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int offset = ((y * size) + x) * 4;
                bgra[offset] = (byte)(x < 32 ? 0x20 : 0xF0);
                bgra[offset + 1] = (byte)(y * 4);
                bgra[offset + 2] = 0x40;
                bgra[offset + 3] = 0xFF;
            }
        }

        byte[] encoded = QoiCodec.Encode(bgra, size, size);
        byte[] decoded = QoiCodec.Decode(encoded, out int width, out int height);

        Assert.Equal(size, width);
        Assert.Equal(size, height);
        Assert.Equal(bgra, decoded);
        Assert.True(encoded.Length < bgra.Length, "flat UI content should compress, not expand");
    }

    [Fact]
    public void QoiIsBitExactForNoisyContent()
    {
        Random random = new(7);
        byte[] bgra = new byte[64 * 64 * 4];
        random.NextBytes(bgra);

        byte[] encoded = QoiCodec.Encode(bgra, 64, 64);
        byte[] decoded = QoiCodec.Decode(encoded, out _, out _);

        Assert.Equal(bgra, decoded);
        Assert.True(encoded.Length <= QoiCodec.MaxEncodedSize(64 * 64));
    }

    [Fact]
    public void QoiHandlesPartialEdgeTiles()
    {
        byte[] bgra = new byte[22 * 64 * 4];
        for (int i = 0; i < bgra.Length; i++)
        {
            bgra[i] = (byte)(i % 97);
        }

        TileBitmap tile = QoiTileCodec.Instance.Decode(QoiCodec.Encode(bgra, 22, 64));
        Assert.Equal(22, tile.Width);
        Assert.Equal(64, tile.Height);
        Assert.Equal(bgra, tile.Bgra);
    }

    [Fact]
    public void BalancedCodecQuantizesButStaysDecodable()
    {
        byte[] bgra = new byte[64 * 64 * 4];
        for (int i = 0; i < bgra.Length; i++)
        {
            bgra[i] = (byte)(i % 255);
        }

        byte[] encoded = QuantizedQoiTileCodec.Instance.Encode(bgra, 64, 64);
        TileBitmap decoded = QuantizedQoiTileCodec.Instance.Decode(encoded);

        Assert.Equal(64, decoded.Width);
        Assert.False(QuantizedQoiTileCodec.Instance.IsLossless);
        Assert.True(QoiTileCodec.Instance.IsLossless);
    }

    [Fact]
    public void ArchiveCodecIsLosslessAndDenserThanQoi()
    {
        byte[] bgra = new byte[64 * 64 * 4];
        Random random = new(21);
        for (int y = 0; y < 64; y++)
        {
            bool flat = y < 40;
            for (int x = 0; x < 64; x++)
            {
                int offset = ((y * 64) + x) * 4;
                bgra[offset] = flat ? (byte)0x1A : (byte)random.Next(256);
                bgra[offset + 1] = flat ? (byte)0x2B : (byte)random.Next(256);
                bgra[offset + 2] = flat ? (byte)0x3C : (byte)random.Next(256);
                bgra[offset + 3] = 0xFF;
            }
        }

        byte[] qoi = QoiTileCodec.Instance.Encode(bgra, 64, 64);
        byte[] archive = TileCodecs.Archive.Encode(bgra, 64, 64);

        Assert.True(TileCodecs.Archive.IsLossless);
        Assert.Equal(3, TileCodecs.Archive.Id);
        Assert.Equal(bgra, TileCodecs.Archive.Decode(archive).Bgra);
        Assert.True(archive.Length < qoi.Length, $"archive {archive.Length} should beat QOI start point {qoi.Length}");
        Assert.Same(TileCodecs.Archive, TileCodecs.ById(3));
        Assert.Same(TileCodecs.Archive, TileCodecs.FromFidelityMode("archive"));
    }

    [Fact]
    public void PngWriterEmitsAValidHeader()
    {
        byte[] bgra = new byte[8 * 8 * 4];
        byte[] png = ScreenRecall.Player.PngWriter.Encode(8, 8, bgra);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(8, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(8, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
    }
}
