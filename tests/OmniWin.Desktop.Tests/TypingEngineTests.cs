using System.Diagnostics;
using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

/// <summary>
/// File 23, A8, A9 and A10 through the real <see cref="DesktopEngine"/> and the real typing pump, with a faked UI Automation side
/// and a fake editor (no window, no input). Real time passes (the settled readback waits 500 ms), so each test takes about a second.
/// </summary>
public sealed class TypingEngineTests
{
    private static DesktopActRequest Type(string text) => new("e1", 0, "type", text);

    // ---------- A8: the settled readback ----------

    [Fact]
    public async Task A_character_whose_original_lands_late_after_the_resend_and_the_first_matching_readback_is_contradicted()
    {
        var backend = new FakeDesktopBackend();
        // 'b' is lost the first time, taken when sent again; the first copy then turns up 300 ms later and doubles it.
        backend.Editor.Policy = (c, n) => c == 'b' ? (n == 1 ? Landing.LandsLate : Landing.Takes) : Landing.Takes;
        backend.Editor.LateAfterMs = 300;
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(Type("ab"), TestContext.Current.CancellationToken);

        Assert.Equal(ActStatus.Contradicted, result.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, result.Reason);
        Assert.True(result.InputSent);
        Assert.Equal(2, backend.Editor.SendsOf('b'));
        Assert.Equal("abb", backend.Editor.Text);   // the late original really did land
    }

    [Fact]
    public async Task A_character_whose_original_never_lands_is_confirmed_only_after_the_text_stayed_unchanged_for_500_ms()
    {
        var backend = new FakeDesktopBackend();
        backend.Editor.Policy = (c, n) => c == 'b' && n == 1 ? Landing.Drops : Landing.Takes;
        await using var engine = backend.NewEngine();

        var sw = Stopwatch.StartNew();
        var result = await engine.ActAsync(Type("ab"), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(2, backend.Editor.SendsOf('b'));
        Assert.Equal("ab", backend.Editor.Text);
        // The readback was repeated for at least 500 ms before "confirmed" (the first read plus about one every 100 ms). The count
        // is only "repeated at all": under a loaded test run each 100 ms pause can take far longer, so fewer reads fit; the 500 ms
        // rule itself is the elapsed-time check below.
        Assert.True(backend.ReadbackCalls >= 2, $"readbacks: {backend.ReadbackCalls}");
        Assert.True(sw.ElapsedMilliseconds >= 480, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Text_typed_without_any_resend_needs_no_settle_wait()
    {
        var backend = new FakeDesktopBackend();
        await using var engine = backend.NewEngine();

        var sw = Stopwatch.StartNew();
        var result = await engine.ActAsync(Type("abc"), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(backend.ReadbackCalls <= 2, $"readbacks: {backend.ReadbackCalls}");
        Assert.True(sw.ElapsedMilliseconds < 400, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_resent_character_whose_text_cannot_be_read_back_is_unverified_never_confirmed()
    {
        var backend = new FakeDesktopBackend();
        backend.Editor.Policy = (c, n) => c == 'b' && n == 1 ? Landing.Drops : Landing.Takes;
        // The first readback matches; after that the control cannot be read (the check then never becomes stable).
        var reads = 0;
        backend.Readback = _ => Interlocked.Increment(ref reads) == 1 ? new ConditionCheck(true, "matches") : null;
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(Type("ab"), TestContext.Current.CancellationToken);

        Assert.Equal(ActStatus.Unverified, result.Status);
        Assert.True(result.InputSent);
    }

    // ---------- the readback function on its own (small times) ----------

    private static Func<CancellationToken, Task<ConditionCheck?>> Reads(params ConditionCheck?[] sequence)
    {
        var i = 0;
        return _ => Task.FromResult(sequence[Math.Min(Interlocked.Increment(ref i) - 1, sequence.Length - 1)]);
    }

    private static readonly ConditionCheck Match = new(true, "match");
    private static readonly ConditionCheck Differs = new(false, "differs", Mismatch: true);

    [Fact]
    public async Task A_text_that_stays_the_same_is_stable_after_the_settle_time()
    {
        var stable = await DesktopEngine.StableReadbackAsync(Reads(Match), 60, 10, 400, CancellationToken.None);
        Assert.True(stable?.Met);
    }

    [Fact]
    public async Task A_text_that_changes_restarts_the_wait_and_the_stable_text_decides()
    {
        // Matches, changes to something else, and stays there: the stable text is the wrong one.
        var stable = await DesktopEngine.StableReadbackAsync(Reads(Match, Match, Differs), 60, 10, 600, CancellationToken.None);
        Assert.True(stable?.Mismatch);
    }

    [Fact]
    public async Task A_text_that_never_stops_changing_has_no_stable_readback_within_the_cap()
    {
        var flip = 0;
        Task<ConditionCheck?> Alternating(CancellationToken _) => Task.FromResult<ConditionCheck?>(Interlocked.Increment(ref flip) % 2 == 0 ? Match : null);

        Assert.Null(await DesktopEngine.StableReadbackAsync(Alternating, 60, 10, 200, CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_text_gives_nothing_stable()
    {
        Assert.Null(await DesktopEngine.StableReadbackAsync(Reads((ConditionCheck?)null), 60, 10, 200, CancellationToken.None));
    }

    // ---------- A9: a character the editor refuses twice ----------

    [Fact]
    public async Task A_character_the_editor_refuses_twice_is_sent_exactly_twice_and_the_act_ends_in_error_readback_mismatch()
    {
        var backend = new FakeDesktopBackend();
        backend.Editor.Policy = (c, _) => c == 'b' ? Landing.Drops : Landing.Takes;
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(Type("abc"), TestContext.Current.CancellationToken);

        Assert.Equal(2, backend.Editor.SendsOf('b'));
        Assert.Equal(0, backend.Editor.SendsOf('c'));   // typing stopped at the refused character
        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, result.Reason);
        Assert.True(result.InputSent);
    }

    // ---------- A10: a long type does not block other calls ----------

    [Fact]
    public async Task A_two_thousand_character_type_through_the_engine_is_confirmed_and_booked_as_dispatch_time()
    {
        var backend = new FakeDesktopBackend();
        await using var engine = backend.NewEngine();
        var text = new string('x', 2000);

        var result = await engine.ActAsync(Type(text), TestContext.Current.CancellationToken);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(text, backend.Editor.Text);
        Assert.Equal(2000, backend.Editor.TotalSends);
        // The time spent sending the characters is dispatch ("input"), not pre-check.
        var phases = result.Timing.PhasesMs;
        Assert.True(phases.GetValueOrDefault("input") > phases.GetValueOrDefault("precheck"),
            $"input {phases.GetValueOrDefault("input"):0.0} ms, precheck {phases.GetValueOrDefault("precheck"):0.0} ms");
    }

    [Fact]
    public async Task An_observe_sent_during_a_long_type_is_answered_between_characters()
    {
        var backend = new FakeDesktopBackend();
        backend.Editor.SendMs = 2;   // each send takes about 15 ms on Windows timers, so 120 characters take about 2 s and the type is still running when the observe arrives
        var reached = new TaskCompletionSource();
        backend.Editor.AfterSend = typed =>
        {
            if (typed >= 50) reached.TrySetResult();
        };
        await using var engine = backend.NewEngine();
        var typing = engine.ActAsync(Type(new string('y', 120)), TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        var observation = await engine.ObserveAsync(new DesktopObserveRequest(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.False(typing.IsCompleted, "the type had already ended: the observe was not sent during it");
        Assert.Equal("w1", observation.Target);
        Assert.InRange(backend.TypedAtLastObserve, 1, 119);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"the observe waited {sw.ElapsedMilliseconds} ms");
        Assert.Equal(ActStatus.Confirmed, (await typing).Status);
    }

    [Fact]
    public async Task An_act_sent_during_a_long_type_gets_an_immediate_blocked_result_that_says_a_type_is_running()
    {
        var backend = new FakeDesktopBackend();
        backend.Editor.SendMs = 2;
        var reached = new TaskCompletionSource();
        backend.Editor.AfterSend = typed =>
        {
            if (typed >= 50) reached.TrySetResult();
        };
        await using var engine = backend.NewEngine();
        var typing = engine.ActAsync(Type(new string('z', 120)), TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        var click = await engine.ActAsync(new DesktopActRequest("e1", 0, "click"), TestContext.Current.CancellationToken);
        var second = await engine.ActAsync(Type("more"), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(ActStatus.Blocked, click.Status);
        Assert.False(click.InputSent);
        Assert.Contains("type", click.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("running", click.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ActStatus.Blocked, second.Status);
        Assert.False(second.InputSent);
        // Contract decision (reviewer, handoff 23): the one use of rate_limit is "blocked while a type holds the lane".
        Assert.Equal(ReasonCodes.RateLimit, click.Reason);
        Assert.Equal(ReasonCodes.RateLimit, second.Reason);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"the two acts waited {sw.ElapsedMilliseconds} ms");
        Assert.False(typing.IsCompleted);
        Assert.Equal(ActStatus.Confirmed, (await typing).Status);
    }

    [Fact]
    public async Task The_lane_is_free_again_after_the_type_ends()
    {
        var backend = new FakeDesktopBackend();
        await using var engine = backend.NewEngine();
        Assert.Equal(ActStatus.Confirmed, (await engine.ActAsync(Type("hi"), TestContext.Current.CancellationToken)).Status);

        var click = await engine.ActAsync(new DesktopActRequest("e1", 0, "click"), TestContext.Current.CancellationToken);

        Assert.NotEqual(ActStatus.Blocked, click.Status);
    }

    [Fact]
    public async Task When_the_typing_limit_runs_out_the_act_reports_engine_timeout_not_cancelled()
    {
        var backend = new FakeDesktopBackend();
        backend.Editor.SendMs = 300;
        // Normal limit 200 ms; the typing limit is that plus 60 ms per character (3 characters: 380 ms), which two 300 ms sends exceed.
        await using var engine = backend.NewEngine(connectionMs: 100, transactionMs: 50);

        var result = await engine.ActAsync(Type("abc"), TestContext.Current.CancellationToken);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.EngineTimeout, result.Reason);
        Assert.True(result.InputSent);
        Assert.Contains("typing time limit", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- A12: D1 through the engine ----------

    private static ElementDescriptor FileNameBox(bool password = false) =>
        new("e1", "desktop", "notepad.exe", "Save as", null, "edit", "File name:", "1148", password, true, []);

    [Theory]
    [InlineData("set_value", "report.txt")]
    [InlineData("clear", null)]
    public async Task Setting_or_clearing_a_file_dialogs_file_name_box_is_unverified_even_when_the_readback_matches(string action, string? value)
    {
        var backend = new FakeDesktopBackend { Target = FileNameBox(), Readback = _ => new ConditionCheck(true, "value readback") };
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(new DesktopActRequest("e1", 0, action, value), TestContext.Current.CancellationToken);

        Assert.Equal(ActStatus.Unverified, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(ValueSettingPolicy.IgnoredDetail, result.Detail);
    }

    [Fact]
    public async Task Setting_an_ordinary_edit_whose_readback_matches_is_still_confirmed()
    {
        var backend = new FakeDesktopBackend { Readback = _ => new ConditionCheck(true, "value readback") };
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(new DesktopActRequest("e1", 0, "set_value", "hello"), TestContext.Current.CancellationToken);

        Assert.Equal(ActStatus.Confirmed, result.Status);
    }

    [Fact]
    public async Task Typing_into_the_file_name_box_is_not_downgraded_because_it_really_goes_through_the_keyboard()
    {
        var backend = new FakeDesktopBackend { Target = FileNameBox() };
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(Type("a.txt"), TestContext.Current.CancellationToken);

        Assert.Equal(ActStatus.Confirmed, result.Status);
    }
}
