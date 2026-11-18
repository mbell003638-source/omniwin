namespace OmniWin.Desktop.Tests;

/// <summary>
/// Parallel lanes: which acts may overlap. Fakes only: no UI Automation, no windows, no input.
/// Real mouse and keyboard input is one lane; UI Automation pattern acts on different windows run together;
/// two acts on the same window never overlap.
/// </summary>
public sealed class ActLanesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<bool> CompletesQuickly(Task task, int ms = 150) =>
        await Task.WhenAny(task, Task.Delay(ms, Ct)) == task;

    [Fact]
    public async Task Two_acts_on_the_same_window_are_serialized()
    {
        using var lanes = new ActLanes();
        using var first = await lanes.EnterWindowAsync("w1", Ct);

        var second = lanes.EnterWindowAsync("w1", Ct);
        Assert.False(await CompletesQuickly(second));   // waits while the first act holds the window

        first.Dispose();
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("w1", lease.WindowKey);
    }

    [Fact]
    public async Task Pattern_acts_on_different_windows_run_at_the_same_time()
    {
        using var lanes = new ActLanes();
        using var a = await lanes.EnterWindowAsync("w1", Ct);

        var b = lanes.EnterWindowAsync("w2", Ct);
        var c = lanes.EnterWindowAsync("w3", Ct);

        Assert.True(await CompletesQuickly(b));
        Assert.True(await CompletesQuickly(c));
        (await b).Dispose();
        (await c).Dispose();
    }

    [Fact]
    public async Task Physical_acts_are_strictly_one_at_a_time()
    {
        using var lanes = new ActLanes();
        using var first = await lanes.EnterPhysicalAsync(Ct);

        var second = lanes.EnterPhysicalAsync(Ct);
        Assert.False(await CompletesQuickly(second));
        Assert.Equal(0, lanes.PhysicalFree);

        first.Dispose();
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(lease.HoldsPhysical);
    }

    [Fact]
    public async Task A_pattern_act_never_waits_for_the_physical_lane()
    {
        using var lanes = new ActLanes();
        using var physical = await lanes.EnterPhysicalAsync(Ct);
        await physical.EnterWindowAsync("w1", Ct);

        // A pattern act on another window runs while a physical act works on w1.
        var other = lanes.EnterWindowAsync("w2", Ct);
        Assert.True(await CompletesQuickly(other));
        (await other).Dispose();
    }

    [Fact]
    public async Task A_physical_act_and_a_pattern_act_on_the_same_window_do_not_overlap_in_either_order()
    {
        using var lanes = new ActLanes();

        // Pattern first: the physical act gets its window lock only after the pattern act ends.
        var pattern = await lanes.EnterWindowAsync("w1", Ct);
        var physical = await lanes.EnterPhysicalAsync(Ct);
        var physicalWindow = physical.EnterWindowAsync("w1", Ct);
        Assert.False(await CompletesQuickly(physicalWindow));
        pattern.Dispose();
        await physicalWindow.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Physical first: a pattern act on that window waits.
        var late = lanes.EnterWindowAsync("w1", Ct);
        Assert.False(await CompletesQuickly(late));
        physical.Dispose();
        (await late.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
    }

    [Fact]
    public async Task The_stop_token_cancels_every_waiting_lane_and_leaks_nothing()
    {
        using var lanes = new ActLanes();
        using var heldWindow = await lanes.EnterWindowAsync("w1", Ct);
        using var heldPhysical = await lanes.EnterPhysicalAsync(Ct);
        using var stop = new CancellationTokenSource();

        var waitingWindow = lanes.EnterWindowAsync("w1", stop.Token);
        var waitingPhysical = lanes.EnterPhysicalAsync(stop.Token);
        Assert.False(await CompletesQuickly(waitingWindow, 50));
        Assert.False(await CompletesQuickly(waitingPhysical, 50));

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingWindow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingPhysical);

        // The cancelled waiters took nothing: once the holders let go, both locks are free again.
        heldWindow.Dispose();
        heldPhysical.Dispose();
        (await lanes.EnterWindowAsync("w1", Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        (await lanes.EnterPhysicalAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
    }

    [Fact]
    public async Task A_cancelled_wait_for_a_window_releases_the_physical_lane_it_already_held()
    {
        using var lanes = new ActLanes();
        using var holder = await lanes.EnterWindowAsync("w1", Ct);
        using var stop = new CancellationTokenSource();

        var physical = await lanes.EnterPhysicalAsync(Ct);
        var wait = physical.EnterWindowAsync("w1", stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        physical.Dispose();   // the engine disposes its lease in a finally block

        Assert.Equal(1, lanes.PhysicalFree);
    }

    [Fact]
    public async Task A_lease_holds_one_window_and_dispose_is_idempotent()
    {
        using var lanes = new ActLanes();
        var lease = await lanes.EnterPhysicalAsync(Ct);
        await lease.EnterWindowAsync("w1", Ct);
        await lease.EnterWindowAsync("w1", Ct);   // same window again: no-op, no second wait

        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.EnterWindowAsync("w2", Ct));

        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, lanes.PhysicalFree);
        (await lanes.EnterWindowAsync("w1", Ct)).Dispose();
    }

    [Fact]
    public async Task One_hundred_parallel_mixed_acts_finish_without_deadlock_and_keep_every_exclusion()
    {
        using var lanes = new ActLanes();
        const int windows = 5;
        var activeInWindow = new int[windows];
        var maxInWindow = new int[windows];
        var physicalActive = 0;
        var maxPhysical = 0;
        var patternAtOnce = 0;
        var maxPatternAtOnce = 0;
        var random = new Random(7);
        var plan = Enumerable.Range(0, 100).Select(_ => (Physical: random.Next(4) == 0, Window: random.Next(windows))).ToArray();

        async Task Run((bool Physical, int Window) step)
        {
            var key = "w" + step.Window;
            using var lease = step.Physical ? await lanes.EnterPhysicalAsync(Ct) : await lanes.EnterWindowAsync(key, Ct);
            if (step.Physical) await lease.EnterWindowAsync(key, Ct);

            var inWindow = Interlocked.Increment(ref activeInWindow[step.Window]);
            InterlockedMax(ref maxInWindow[step.Window], inWindow);
            if (step.Physical)
            {
                InterlockedMax(ref maxPhysical, Interlocked.Increment(ref physicalActive));
            }
            else
            {
                InterlockedMax(ref maxPatternAtOnce, Interlocked.Increment(ref patternAtOnce));
            }

            await Task.Delay(2, Ct);   // the act's gate and verification waits

            if (step.Physical) Interlocked.Decrement(ref physicalActive);
            else Interlocked.Decrement(ref patternAtOnce);
            Interlocked.Decrement(ref activeInWindow[step.Window]);
        }

        var all = Task.WhenAll(plan.Select(step => Task.Run(() => Run(step), Ct)));
        await all.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.All(maxInWindow, max => Assert.Equal(1, max));   // never two acts on one window
        Assert.Equal(1, maxPhysical);                           // never two physical acts
        Assert.True(maxPatternAtOnce > 1, "pattern acts on different windows should have overlapped");
        Assert.Equal(1, lanes.PhysicalFree);
    }

    [Fact]
    public async Task The_window_lock_comes_from_the_ref_so_refs_from_two_windows_never_share_a_lock()
    {
        // The engine asks the index which window a ref belongs to; refs stay unique across windows even when
        // two windows are scanned in alternation, so no two windows can end up sharing a lock by mistake.
        var index = new DesktopIndex();
        index.UpdateWindows([DesktopIndexTests.Window(100), DesktopIndexTests.Window(200, "other.exe")]);
        var first = index.ByHandle(100)!;
        var second = index.ByHandle(200)!;

        var seen = new List<(string Ref, string Window, string Identity)>();
        for (var round = 0; round < 5; round++)
        {
            foreach (var window in new[] { first, second })
            {
                foreach (var element in index.UpdateElements(window, [DesktopIndexTests.Root(), DesktopIndexTests.Button("b" + round)], 0, 0).Elements)
                {
                    seen.Add((element.Ref, window.Id, element.Sample.Identity));
                }
            }
        }

        // Rescanning an element keeps its ref, and two different elements never share a ref (no ref reuse).
        var distinctElements = seen.Select(x => (x.Window, x.Identity)).Distinct().Count();
        Assert.Equal(distinctElements, seen.Select(x => x.Ref).Distinct().Count());
        foreach (var group in seen.GroupBy(x => x.Ref))
        {
            Assert.Single(group.Select(x => x.Window).Distinct());                 // one ref, one window
            Assert.Equal(group.Key, group.First().Ref);
            Assert.Equal(group.First().Window, index.FindRef(group.Key, out _)?.Id);  // the ref names its own window
        }

        await Task.CompletedTask;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    // The parallel lanes are off by default until the reviewer signs them off: every act uses the physical lane.
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void Pattern_acts_wait_for_the_physical_lane_unless_parallel_lanes_are_turned_on(bool sharedInput, bool parallel, bool physical)
    {
        Assert.Equal(physical, DesktopEngine.UsesPhysicalLane(sharedInput, parallel));
    }
}
