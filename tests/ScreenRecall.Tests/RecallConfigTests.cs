using System.Reflection;
using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// Config plumbing: the copy and the clamp. Both matter because the capture loop reads its switches from a clone,
/// so a property that does not survive <see cref="RecallConfig.Clone"/> is a setting the user cannot actually set.
/// </summary>
public sealed class RecallConfigTests
{
    /// <summary>
    /// Guards <see cref="Clone"/> against the failure mode it just had: a hand-written copy that silently drops
    /// whatever property was added last. <c>MaxPaceMs</c> was being discarded this way — the governor's ceiling was
    /// reset to its default on every clone and nothing failed — and <c>DetailedTiming</c> would have repeated it.
    /// Reflection means the next property added cannot slip through.
    /// </summary>
    [Fact]
    public void CloneCopiesEveryProperty()
    {
        RecallConfig original = new()
        {
            Version = 7,
            StoragePath = @"C:\somewhere",
            RetentionDays = 11,
            FidelityMode = "balanced",
            TileSize = 128,
            IdlePollMs = 1500,
            BurstPollMs = 25,
            MaxPaceMs = 750,
            DetailedTiming = true,
            CheckpointSeconds = 45,
            CaptureAllMonitors = false,
            EncryptionAtRest = true,
            CaptureGroundTruth = true,
            PauseOnBattery = true,
            MaxDailyMegabytes = 4096,
            MinFreeDiskMegabytes = 512,
            Paused = true,
            PauseHotkey = "Ctrl+Alt+Q",
        };
        original.MonitorIds.Add(3);
        original.ExcludedProcesses.Add("secret.exe");
        original.ExcludedTitlePatterns.Add("incognito");

        RecallConfig copy = original.Clone();

        foreach (PropertyInfo property in typeof(RecallConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? expected = property.GetValue(original);
            object? actual = property.GetValue(copy);

            if (expected is System.Collections.IEnumerable expectedItems and not string)
            {
                Assert.Equal(expectedItems.Cast<object>(), ((System.Collections.IEnumerable)actual!).Cast<object>());
                continue;
            }

            Assert.Equal(expected, actual);
        }

        // Lists must be copies, not shared references: the dashboard stages edits on a clone.
        Assert.NotSame(original.MonitorIds, copy.MonitorIds);
        Assert.NotSame(original.ExcludedProcesses, copy.ExcludedProcesses);
        Assert.NotSame(original.ExcludedTitlePatterns, copy.ExcludedTitlePatterns);
    }

    [Fact]
    public void NormalizeClampsThePacingAndTimingSwitches()
    {
        RecallConfig config = new() { MaxPaceMs = -5, IdlePollMs = 1, BurstPollMs = 100_000 };

        config.Normalize();

        Assert.Equal(0, config.MaxPaceMs);
        Assert.Equal(50, config.IdlePollMs);
        Assert.Equal(500, config.BurstPollMs);
    }

    /// <summary>
    /// Detailed timing is a diagnostic switch: a fresh config must leave it off, or production pays the
    /// instrumentation cost the §5a trace measured at 23% of the capture thread.
    /// </summary>
    [Fact]
    public void DetailedTimingIsOffByDefault()
    {
        Assert.False(new RecallConfig().DetailedTiming);
        Assert.False(new RecallConfig().Normalize().DetailedTiming);
    }
}
