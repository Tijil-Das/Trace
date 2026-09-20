using System.Buffers.Binary;

namespace ScreenRecall.Storage;

public sealed partial class AssetStore
{
    /// <summary>Reads just the header of an asset.</summary>
    public bool TryReadInfo(ulong hash, out AssetInfo info)
    {
        info = new AssetInfo(hash, 0, 0, 0, 0);
        using FileStream? stream = OpenRead(PathFor(hash));
        if (stream is null)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        return ReadHeader(stream, header, hash, out info);
    }

    /// <summary>Reads the payload bytes of an asset.</summary>
    public byte[] ReadPayload(ulong hash, out byte codecId, out int width, out int height)
    {
        string path = PathFor(hash);
        using FileStream stream = OpenRead(path)
            ?? throw new FileNotFoundException($"Asset {TileHash.ToHex(hash)} not found.", path);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (!ReadHeader(stream, header, hash, out AssetInfo info))
        {
            throw new InvalidDataException($"Asset {TileHash.ToHex(hash)} is corrupted (header mismatch).");
        }

        byte[] payload = new byte[info.PayloadLength];
        stream.ReadExactly(payload);
        codecId = info.CodecId;
        width = info.Width;
        height = info.Height;
        return payload;
    }

    /// <summary>
    /// Decodes an asset into a BGRA tile. With <paramref name="verifyHash"/> the decoded pixels are
    /// re-hashed and compared against the file name — the integrity check behind <c>verify</c>.
    /// </summary>
    public TileBitmap TryLoadTile(ulong hash, bool verifyHash = false)
    {
        byte[] payload = ReadPayload(hash, out byte codecId, out int width, out int height);
        TileBitmap decoded = TileCodecs.ById(codecId).Decode(payload);
        if (decoded.Width != width || decoded.Height != height)
        {
            throw new InvalidDataException(
                $"Asset {TileHash.ToHex(hash)} decoded to {decoded.Width}x{decoded.Height}, header says {width}x{height}.");
        }

        if (verifyHash && TileHash.Compute(decoded.Bgra, width, height) != hash)
        {
            throw new InvalidDataException(
                $"Asset {TileHash.ToHex(hash)} does not match its content hash (corruption or collision).");
        }

        return decoded;
    }

    internal static bool ReadHeader(Stream stream, Span<byte> header, ulong expectedHash, out AssetInfo info)
    {
        info = new AssetInfo(expectedHash, 0, 0, 0, 0);
        if (stream.Length < HeaderSize)
        {
            return false;
        }

        stream.ReadExactly(header);
        if (header[0] != 'S' || header[1] != 'R' || header[2] != 'A' || header[3] != 'S' || header[4] != '1')
        {
            return false;
        }

        ulong storedHash = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        if (storedHash != expectedHash)
        {
            return false;
        }

        info = new AssetInfo(
            storedHash,
            header[5],
            BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]));
        return true;
    }

    private static FileStream? OpenRead(string path)
        => File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024)
            : null;
}
