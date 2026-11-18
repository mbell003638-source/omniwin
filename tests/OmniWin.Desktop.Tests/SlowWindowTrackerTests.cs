using System.Runtime.InteropServices;

namespace OmniWin.Desktop.Tests;

/// <summary>D4: a fake slow window (a fake clock and fake read times, no real UI Automation) is skipped and recovers.</summary>
public sealed class SlowWindowTrackerTests
{
    private sealed class Clock
    {
        internal TimeSpan Now { get; set; }
    }

    private static (SlowWindowTracker Tracker, Clock Clock) Create(int limitMs = 1500, int coolDownSeconds = 30)
    {
        var clock = new Clock();
        return (new SlowWindowTracker(() => clock.Now, TimeSpan.FromMilliseconds(limitMs), TimeSpan.FromSeconds(coolDownSeconds)), clock);
    }

    [Fact]
    public void A_read_within_the_limit_is_not_slow()
    {
        var (tracker, _) = Create();
        Assert.False(tracker.Observe(1, 10, 100, TimeSpan.FromMilliseconds(1500)));
        Assert.False(tracker.IsSlow(1, 10, 100));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void A_fake_slow_window_is_skipped_while_other_windows_stay_readable()
    {
        var (tracker, _) = Create();
        Assert.True(tracker.Observe(1, 10, 100, TimeSpan.FromSeconds(18)));   // Explorer taskbar: about 18 s

        Assert.True(tracker.IsSlow(1, 10, 100));
        Assert.False(tracker.IsSlow(2, 20, 200));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void A_timed_out_read_marks_the_window_slow()
    {
        var (tracker, _) = Create();
        tracker.Mark(5, 50, 500);
        Assert.True(tracker.IsSlow(5, 50, 500));
    }

    [Fact]
    public void The_window_is_probed_again_after_the_cool_down()
    {
        var (tracker, clock) = Create(coolDownSeconds: 30);
        tracker.Mark(1, 10, 100);

        clock.Now = TimeSpan.FromSeconds(29.9);
        Assert.True(tracker.IsSlow(1, 10, 100));

        clock.Now = TimeSpan.FromSeconds(30);
        Assert.False(tracker.IsSlow(1, 10, 100));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void A_new_process_behind_the_same_handle_is_not_treated_as_slow()
    {
        var (tracker, _) = Create();
        tracker.Mark(1, 10, 100);

        Assert.False(tracker.IsSlow(1, 11, 100));
        Assert.False(tracker.IsSlow(1, 10, 101));
        Assert.False(tracker.IsSlow(1, 10, 100));   // the stale entry was dropped with the first mismatch
    }

    [Fact]
    public void Closed_windows_are_forgotten()
    {
        var (tracker, _) = Create();
        tracker.Mark(1, 10, 100);
        tracker.Mark(2, 20, 200);

        tracker.Retain(new HashSet<long> { 2 });

        Assert.False(tracker.IsSlow(1, 10, 100));
        Assert.True(tracker.IsSlow(2, 20, 200));
    }

    [Theory]
    [InlineData(0x80131505, true)]    // UIA_E_TIMEOUT
    [InlineData(0x8001011F, true)]    // RPC_E_TIMEOUT
    [InlineData(0x80040201, false)]   // UIA_E_ELEMENTNOTAVAILABLE is handled separately
    [InlineData(0x80004005, false)]
    public void Only_uia_timeouts_count_as_a_slow_window(uint hresult, bool expected)
    {
        Assert.Equal(expected, UiaDesktop.UiaTimedOut(new COMException("x", unchecked((int)hresult))));
    }
}
