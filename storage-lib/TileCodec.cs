namespace ScreenRecall.Storage;

/// <summary>A decoded tile: tightly packed BGRA pixels.</summary>
public sealed class TileBitmap
{
    public TileBitmap(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Tightly packed BGRA pixels, length == Width * Height * 4.</summary>
    public byte[] Bgra { get; }

    public int Stride => Width * 4;
}

/// <summary>Tile payload codec (spec 5.4). Pluggable so a WebP backend can be added later.</summary>
public interface ITileCodec
{
    /// <summary>Identifier persisted in the asset header.</summary>
    byte Id { get; }

    /// <summary>Human readable name.</summary>
    string Name { get; }

    /// <summary>True when the codec reproduces pixels exactly.</summary>
    bool IsLossless { get; }

    /// <summary>Encodes a tightly packed BGRA tile.</summary>
    byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height);

    /// <summary>Decodes payload bytes into a BGRA tile.</summary>
    TileBitmap Decode(ReadOnlySpan<byte> payload);
}

/// <summary>Lossless QOI tile codec — the default (spec 5.4).</summary>
public sealed class QoiTileCodec : ITileCodec
{
    public static QoiTileCodec Instance { get; } = new();

    public byte Id => QoiCodec.CodecId;

    public string Name => "qoi-lossless";

    public bool IsLossless => true;

    public byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height) => QoiCodec.Encode(bgra, width, height);

    public TileBitmap Decode(ReadOnlySpan<byte> payload)
    {
        byte[] pixels = QoiCodec.Decode(payload, out int width, out int height);
        return new TileBitmap(width, height, pixels);
    }
}

/// <summary>
/// "Balanced" fidelity mode (spec 5.4): 5-6-5 colour quantisation before QOI — visibly near-lossless
/// on UI content, materially smaller on photographic content. Not the default; lossless stays default.
/// </summary>
public sealed class QuantizedQoiTileCodec : ITileCodec
{
    public static QuantizedQoiTileCodec Instance { get; } = new();

    public byte Id => 2;

    public string Name => "qoi-rgb565";

    public bool IsLossless => false;

    public byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height)
    {
        byte[] quantized = new byte[width * height * 4];
        bgra.Slice(0, quantized.Length).CopyTo(quantized);
        for (int i = 0; i < quantized.Length; i += 4)
        {
            quantized[i] = (byte)(quantized[i] & 0xF8);         // B
            quantized[i + 1] = (byte)(quantized[i + 1] & 0xFC); // G
            quantized[i + 2] = (byte)(quantized[i + 2] & 0xF8); // R
        }

        return QoiCodec.Encode(quantized, width, height);
    }

    public TileBitmap Decode(ReadOnlySpan<byte> payload) => QoiTileCodec.Instance.Decode(payload);
}

/// <summary>Codec registry keyed by the id stored in asset headers.</summary>
public static class TileCodecs
{
    public static ITileCodec Lossless => QoiTileCodec.Instance;

    public static ITileCodec Balanced => QuantizedQoiTileCodec.Instance;

    public static ITileCodec ById(byte id) => id switch
    {
        QoiCodec.CodecId => QoiTileCodec.Instance,
        2 => QuantizedQoiTileCodec.Instance,
        _ => throw new NotSupportedException($"Unknown tile codec id {id}."),
    };

    /// <summary>Resolves a fidelity mode name ("lossless" | "balanced") to a codec.</summary>
    public static ITileCodec FromFidelityMode(string? mode)
        => string.Equals(mode, "balanced", StringComparison.OrdinalIgnoreCase) ? Balanced : Lossless;
}
