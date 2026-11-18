using OmniWin.Core;

namespace OmniWin.Core.Tests;

public sealed class RollingMetricsTests
{
    [Fact]
    public void Nearest_rank_percentiles()
    {
        var m = new RollingMetrics();
        for (var i = 1; i <= 100; i++)
        {
            m.Record("op", i);
        }

        var s = m.Snapshot().Ops["op"];
        Assert.Equal(50, s.P50);
        Assert.Equal(95, s.P95);
        Assert.Equal(99, s.P99);
        Assert.Equal(100, s.Count);
    }

    [Fact]
    public void Keeps_only_the_latest_window_but_counts_everything()
    {
        var m = new RollingMetrics();
        for (var i = 0; i < RollingMetrics.Capacity; i++)
        {
            m.Record("op", 1000);
        }

        for (var i = 0; i < RollingMetrics.Capacity; i++)
        {
            m.Record("op", 1);
        }

        var s = m.Snapshot().Ops["op"];
        Assert.Equal(1, s.P99);
        Assert.Equal(2L * RollingMetrics.Capacity, s.Count);
    }

    [Fact]
    public async Task Concurrent_records_are_all_counted()
    {
        var m = new RollingMetrics();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                m.Record("op", i);
            }
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(8000, m.Snapshot().Ops["op"].Count);
    }
}
