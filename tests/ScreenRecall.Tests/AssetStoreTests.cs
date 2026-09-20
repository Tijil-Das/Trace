using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>Content-addressable store behaviour and dedupe.</summary>
public sealed class AssetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "screenrecall-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static (ulong Hash, int Width, int Height, byte[] Pixels) MakeTile(byte seed, int width = 64, int height = 64)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * seed) % 251);
        }

        return (TileHash.Compute(pixels, width, height), width, height, pixels);
    }

    [Fact]
    public void StoreIsContentAddressedAndDeduplicates()
    {
        AssetStore store = new(SessionLayout.AssetsRoot(_root));
        (ulong hash, int width, int height, byte[] pixels) = MakeTile(3);
        byte[] payload = QoiTileCodec.Instance.Encode(pixels, width, height);

        Assert.True(store.Store(hash, QoiTileCodec.Instance.Id, width, height, payload));
        Assert.False(store.Store(hash, QoiTileCodec.Instance.Id, width, height, payload)); // second write dedupes
        Assert.True(store.Contains(hash));

        string path = store.PathFor(hash);
        Assert.StartsWith(
            Path.Combine(SessionLayout.AssetsRoot(_root), TileHash.ToHex(hash)[..2]),
            path,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.EnumerateFiles(SessionLayout.AssetsRoot(_root), "*.part", SearchOption.AllDirectories).Any());

        TileBitmap tile = store.TryLoadTile(hash, verifyHash: true);
        Assert.Equal(pixels, tile.Bgra);
    }

    [Fact]
    public void EnumerationIsSortedByHashForMergeBasedGc()
    {
        AssetStore store = new(SessionLayout.AssetsRoot(_root));
        List<ulong> hashes = new();
        for (byte seed = 1; seed <= 25; seed++)
        {
            (ulong hash, int width, int height, byte[] pixels) = MakeTile(seed);
            hashes.Add(hash);
            store.Store(hash, QoiTileCodec.Instance.Id, width, height, QoiTileCodec.Instance.Encode(pixels, width, height));
        }

        List<ulong> sorted = store.EnumerateFilesSorted().Select(entry => entry.Hash).ToList();
        Assert.Equal(hashes.Count, sorted.Count);
        Assert.Equal(sorted.OrderBy(hash => hash), sorted);
    }

    [Fact]
    public void CorruptedAssetIsRejectedWhenVerified()
    {
        AssetStore store = new(SessionLayout.AssetsRoot(_root));
        (ulong hash, int width, int height, byte[] pixels) = MakeTile(5);
        store.Store(hash, QoiTileCodec.Instance.Id, width, height, QoiTileCodec.Instance.Encode(pixels, width, height));

        // Overwrite the payload, keeping the header valid: only content verification can catch this.
        string path = store.PathFor(hash);
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(AssetStore.HeaderSize, SeekOrigin.Begin);
            stream.Write(new byte[32]);
        }

        Assert.Throws<InvalidDataException>(() => store.TryLoadTile(hash, verifyHash: true));
    }
}
