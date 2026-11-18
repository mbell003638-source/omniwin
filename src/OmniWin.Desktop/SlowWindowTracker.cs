using System.Diagnostics;

namespace OmniWin.Desktop;

/// <summary>
/// D4: one window whose UI Automation provider answers very slowly (Explorer's taskbar can take about 18 s) must not make
/// every window list or observe fail. A window whose read exceeds <see cref="Limit"/>, or whose read times out, is remembered
/// for <see cref="CoolDown"/>; while remembered it is listed as <c>inaccessible=slow</c> without another UI Automation call.
/// When the cool-down ends the window is probed once more, so a recovered window comes back by itself.
/// </summary>
internal sealed class SlowWindowTracker
{
    internal static readonly TimeSpan DefaultLimit = TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan DefaultCoolDown = TimeSpan.FromSeconds(30);

    private readonly Func<TimeSpan> _now;
    private readonly Dictionary<long, Entry> _slow = new();

    internal SlowWindowTracker(Func<TimeSpan>? now = null, TimeSpan? limit = null, TimeSpan? coolDown = null)
    {
        var start = Stopwatch.GetTimestamp();
        _now = now ?? (() => Stopwatch.GetElapsedTime(start));
        Limit = limit ?? DefaultLimit;
        CoolDown = coolDown ?? DefaultCoolDown;
    }

    internal TimeSpan Limit { get; }
    internal TimeSpan CoolDown { get; }

    /// <summary>Number of windows currently skipped as slow.</summary>
    internal int Count
    {
        get
        {
            Prune();
            return _slow.Count;
        }
    }

    /// <summary>True when this exact window (same process instance) is inside its cool-down.</summary>
    internal bool IsSlow(long hwnd, int pid, long processStart)
    {
        if (!_slow.TryGetValue(hwnd, out var entry)) return false;
        if (entry.Pid != pid || entry.ProcessStart != processStart || _now() - entry.Since >= CoolDown)
        {
            _slow.Remove(hwnd);
            return false;
        }

        return true;
    }

    /// <summary>Records the time a read took; returns true when it was slow enough that the window is now skipped.</summary>
    internal bool Observe(long hwnd, int pid, long processStart, TimeSpan elapsed)
    {
        if (elapsed <= Limit) return false;
        Mark(hwnd, pid, processStart);
        return true;
    }

    internal void Mark(long hwnd, int pid, long processStart) =>
        _slow[hwnd] = new Entry(pid, processStart, _now());

    /// <summary>Forgets windows that no longer exist.</summary>
    internal void Retain(ISet<long> liveHandles)
    {
        foreach (var hwnd in _slow.Keys.ToArray())
        {
            if (!liveHandles.Contains(hwnd)) _slow.Remove(hwnd);
        }
    }

    private void Prune()
    {
        var now = _now();
        foreach (var (hwnd, entry) in _slow.ToArray())
        {
            if (now - entry.Since >= CoolDown) _slow.Remove(hwnd);
        }
    }

    private readonly record struct Entry(int Pid, long ProcessStart, TimeSpan Since);
}
