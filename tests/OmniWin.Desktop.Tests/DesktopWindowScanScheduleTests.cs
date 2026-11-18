namespace OmniWin.Desktop.Tests;

public sealed class DesktopWindowScanScheduleTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, true)]
    public void First_explicit_and_healthy_event_scans_run_without_waiting_for_the_fallback_interval(
        bool fresh, bool initialized, bool eventsAvailable, bool windowEventChanged)
    {
        Assert.True(UiaDesktop.ShouldScanWindowUia(fresh, initialized, eventsAvailable,
            windowEventChanged, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(25, true, false)]
    [InlineData(999, false, false)]
    [InlineData(999, true, false)]
    [InlineData(1000, false, true)]
    [InlineData(1000, true, true)]
    [InlineData(1001, false, true)]
    public void Missing_events_limit_only_full_uia_scans_to_one_second(
        int elapsedMs, bool windowEventChanged, bool expected)
    {
        Assert.Equal(expected, UiaDesktop.ShouldScanWindowUia(false, true, false,
            windowEventChanged, TimeSpan.FromMilliseconds(elapsedMs)));
    }

    [Fact]
    public void Quiet_healthy_event_discovery_stays_native_when_only_the_fallback_ttl_expires()
    {
        Assert.False(UiaDesktop.ShouldScanWindowUia(false, true, true, false, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Frequent_native_fallback_refreshes_do_not_restart_the_full_uia_scan_clock()
    {
        var lastFullScan = TimeSpan.Zero;
        var scans = new List<TimeSpan>();
        for (var refresh = 1; refresh <= 200; refresh++)
        {
            var now = TimeSpan.FromMilliseconds(refresh * 35);
            if (UiaDesktop.ShouldScanWindowUia(false, true, false, true, now - lastFullScan))
            {
                scans.Add(now);
                Assert.True(now - lastFullScan >= TimeSpan.FromSeconds(1));
                lastFullScan = now;
            }
        }

        Assert.Equal(6, scans.Count);
        Assert.True(lastFullScan > TimeSpan.FromSeconds(6));
    }
}
