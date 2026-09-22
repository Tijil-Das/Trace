using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// A session's meta holds one row per (monitor id, geometry), because a monitor can change size mid-session and a
/// fallback/test source can record a different desktop under the same id. Readers must resolve the geometry in
/// force <em>at the timestamp they are replaying</em>: two rows for one id mean two tile grids, and tile (x, y)
/// under the wrong grid lands somewhere it never was. A real store ended up with exactly that and replayed 689 MB
/// of a 1366x768 desktop as repeating stripes on a 1024x768 grid.
/// </summary>
public sealed class SessionMetaTests
{
    private const long T0 = 1_790_000_000_000;
    private static MonitorInfo Real => new(0, "\\\\.\\DISPLAY1", 0, 0, 1366, 768, 64);
    private static MonitorInfo Foreign => new(0, "synthetic", 0, 0, 1024, 768, 64);

    private static SessionMeta MetaWithBothGeometries()
    {
        SessionMeta meta = new();

        // The real display is seen from the start, a foreign grid briefly in the middle, then the real display
        // again: exactly the shape the live store ended up with when the fallback recorded for two minutes.
        meta.Update(new[] { Real }, T0);
        meta.Update(new[] { Foreign }, T0 + 10_000);
        meta.Update(new[] { Real }, T0 + 100_000);
        return meta;
    }

    [Fact]
    public void TheNewestGeometryWinsRatherThanTheLastRowInTheFile()
    {
        SessionMeta meta = MetaWithBothGeometries();

        // The foreign row is appended after the real one, so file order would pick it - the bug. Recency must.
        MonitorInfo? newest = meta.AllMonitors().Single();
        Assert.Equal(1366, newest!.Width);
        Assert.Equal(22, newest.Columns);
    }

    [Fact]
    public void GeometryIsResolvedPerTimestamp()
    {
        SessionMeta meta = MetaWithBothGeometries();

        // Inside a sighting's own window, the most recently *changed* geometry is the one in force.
        Assert.Equal(1366, meta.GeometryAt(0, T0)!.Width);
        Assert.Equal(1024, meta.GeometryAt(0, T0 + 10_000)!.Width);

        // The foreign row was only ever seen at T0+10s, so the very next millisecond it is no longer evidence of
        // anything: the real display's window still covers that moment, and it is the one still in force.
        Assert.Equal(1366, meta.GeometryAt(0, T0 + 10_001)!.Width);

        // After every window has closed, the newest last-seen wins - the display capture kept using.
        Assert.Equal(1366, meta.GeometryAt(0, T0 + 100_000)!.Width);
        Assert.Equal(1366, meta.GeometryAt(0, T0 + 500_000)!.Width);

        // Before the first sighting there is no geometry to apply, and a monitor that never existed has none.
        Assert.Null(meta.GeometryAt(0, T0 - 1));
        Assert.Null(meta.GeometryAt(7, T0 + 100_000));
    }

    [Fact]
    public void TheGeometryWindowTravelsWithTheRow()
    {
        SessionMeta meta = MetaWithBothGeometries();

        SessionMetaMonitor? foreign = meta.MonitorAt(0, T0 + 10_000);
        Assert.NotNull(foreign);
        Assert.Equal(1024, foreign!.Width);
        Assert.Equal(T0 + 10_000, foreign.FirstSeenUnixMs);
        Assert.Equal(T0 + 10_000, foreign.LastSeenUnixMs);
    }

    [Fact]
    public void MonitorsAtReportsOneEntryPerId()
    {
        SessionMeta meta = MetaWithBothGeometries();
        meta.Update(new[] { new MonitorInfo(1, "second", 1366, 0, 1920, 1080, 64) }, T0 + 100_000);

        // Both ids are reported at a moment after every window, and the foreign grid still wins while it is the
        // geometry actually in force.
        IReadOnlyList<MonitorInfo> both = meta.MonitorsAt(T0 + 100_000);
        Assert.Equal(2, both.Count);
        Assert.Equal(1366, both.Single(monitor => monitor.Id == 0).Width);
        Assert.Equal(1920, both.Single(monitor => monitor.Id == 1).Width);

        // At T0+10s both rows for id 0 still cover that instant (the real row is re-sighted later, which widens
        // its window backwards-naively), but one entry per id is the contract: the most recently *changed* row,
        // the foreign grid, is the one in force while it is being recorded. Id 1 is not known yet at that point.
        Assert.Equal(1024, meta.MonitorsAt(T0 + 10_000).Single().Width);
        Assert.Equal(1366, meta.MonitorsAt(T0 + 500_000).Single(monitor => monitor.Id == 0).Width);
    }

    [Fact]
    public void MergingTheSameGeometryThriceKeepsOneRow()
    {
        SessionMeta meta = new();
        meta.Update(new[] { Real }, T0);
        meta.Update(new[] { Real }, T0 + 5_000);
        meta.Update(new[] { Real }, T0 + 9_000);

        SessionMetaMonitor row = Assert.Single(meta.Monitors);
        Assert.Equal(T0, row.FirstSeenUnixMs);
        Assert.Equal(T0 + 9_000, row.LastSeenUnixMs);
    }
}


