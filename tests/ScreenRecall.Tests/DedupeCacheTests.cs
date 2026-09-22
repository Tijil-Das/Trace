using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// The dedupe cache exists to keep the capture thread off the file system. What matters is therefore not that it is
/// large but that it never forgets everything at once: a miss on this path is a filesystem existence probe, per
/// tile, on the thread that is supposed to be recording the screen.
/// </summary>
public sealed class DedupeCacheTests
{
    [Fact]
    public void AHashSurvivesTheGenerationRotation()
    {
        DedupeCache cache = new(capacity: 2048); // two generations of 1024
        const ulong first = 0xA11CE;
        cache.Add(first);

        for (ulong hash = 1; hash <= 1024; hash++)
        {
            cache.Add(hash);
        }

        // The first hash was pushed into the older generation by the rotation, not wiped along with every other
        // hash, which is what the old clear-everything-at-the-cap behaviour did.
        Assert.True(cache.Contains(first));
        Assert.True(cache.Count <= 2048);
    }

    [Fact]
    public void ALookupIsAnsweredFromMemoryOrCountedAsAMiss()
    {
        DedupeCache cache = new();
        cache.Add(7);

        Assert.True(cache.Contains(7));
        Assert.False(cache.Contains(8));

        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void ClearingForgetsEverything()
    {
        DedupeCache cache = new();
        cache.Add(7);
        cache.Clear();

        Assert.False(cache.Contains(7));
        Assert.Equal(0, cache.Count);
    }
}
