using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

/// <summary>
/// File 23, A11 and A12. The F24 tap is checked again right before it and reported as a key; a lost tap cannot hide behind an
/// exception; the typing pump waits per character (caret moves: next character; caret stuck: one re-send, then the error).
/// Fakes only: no window is touched and no input is sent. "Desktop input" because the native seams are static.
/// </summary>
[Collection("Desktop input")]
public sealed class ForegroundAndPumpTests
{
    // ---------- A11: the tap is decided again right before it ----------

    private static bool TapAllowed(long front, bool responds, string? process)
    {
        var (oldFront, oldResponds, oldName) = (NativeDesktop.FrontWindow, NativeDesktop.Responds, NativeDesktop.ProcessNameOf);
        try
        {
            NativeDesktop.FrontWindow = () => front;
            NativeDesktop.Responds = _ => responds;
            NativeDesktop.ProcessNameOf = _ => process;
            return NativeDesktop.TapAllowedNow();
        }
        finally
        {
            (NativeDesktop.FrontWindow, NativeDesktop.Responds, NativeDesktop.ProcessNameOf) = (oldFront, oldResponds, oldName);
        }
    }

    [Fact]
    public void The_tap_is_allowed_when_an_ordinary_window_that_answers_is_in_front() =>
        Assert.True(TapAllowed(front: 77, responds: true, process: "notepad"));

    [Theory]
    [InlineData("mstsc")]
    [InlineData("msrdc.exe")]
    [InlineData("vmconnect")]
    public void The_tap_is_refused_when_a_forwarding_client_is_in_front_at_that_moment(string client) =>
        Assert.False(TapAllowed(front: 77, responds: true, process: client));

    [Fact]
    public void The_tap_is_refused_when_the_window_in_front_does_not_answer_WM_NULL() =>
        Assert.False(TapAllowed(front: 77, responds: false, process: "notepad"));

    [Fact]
    public void The_check_looks_at_the_window_in_front_now_not_the_one_seen_earlier()
    {
        var oldFront = NativeDesktop.FrontWindow;
        var oldName = NativeDesktop.ProcessNameOf;
        var oldResponds = NativeDesktop.Responds;
        try
        {
            NativeDesktop.Responds = _ => true;
            NativeDesktop.ProcessNameOf = hwnd => hwnd == 1 ? "notepad" : "mstsc";
            NativeDesktop.FrontWindow = () => 1;
            Assert.True(NativeDesktop.TapAllowedNow());
            NativeDesktop.FrontWindow = () => 2;   // the front window changed in between
            Assert.False(NativeDesktop.TapAllowedNow());
        }
        finally
        {
            (NativeDesktop.FrontWindow, NativeDesktop.ProcessNameOf, NativeDesktop.Responds) = (oldFront, oldName, oldResponds);
        }
    }

    // ---------- A11: what a focus attempt reports, also when it ends by throwing ----------

    [Fact]
    public async Task A_focus_that_tapped_F24_is_reported_by_BringToForeground_with_the_key_and_input()
    {
        var backend = new FakeDesktopBackend
        {
            ForegroundImpl = _ =>
            {
                return true;
            },
        };
        backend.LastForegroundAttempt = (true, true);
        await using var engine = backend.NewEngine();

        var attempt = await engine.BringToForegroundDetailedAsync(0x1234, TestContext.Current.CancellationToken);

        Assert.True(attempt.Verified);
        Assert.True(attempt.InputSent);
        Assert.True(attempt.KeyTapped);
    }

    [Fact]
    public async Task A_focus_that_sent_nothing_reports_no_input_and_no_key()
    {
        var backend = new FakeDesktopBackend();
        backend.LastForegroundAttempt = (false, false);
        await using var engine = backend.NewEngine();

        var attempt = await engine.BringToForegroundDetailedAsync(0x1234, TestContext.Current.CancellationToken);

        Assert.True(attempt.Verified);
        Assert.False(attempt.InputSent);
        Assert.False(attempt.KeyTapped);
    }

    [Fact]
    public async Task A_boundary_that_throws_after_the_F24_tap_does_not_hide_the_tap()
    {
        var backend = new FakeDesktopBackend
        {
            ForegroundImpl = _ => throw new DesktopFault(ActStatus.Blocked, ReasonCodes.UserActive, "A modifier is active."),
        };
        backend.LastForegroundAttempt = (true, true);   // what the attempt had recorded before it stopped
        await using var engine = backend.NewEngine();

        var attempt = await engine.BringToForegroundDetailedAsync(0x1234, TestContext.Current.CancellationToken);

        Assert.False(attempt.Verified);
        Assert.True(attempt.InputSent);
        Assert.True(attempt.KeyTapped);
    }

    [Fact]
    public async Task A_boundary_stop_before_any_input_reports_the_stops_reason_and_input_sent_false()
    {
        var backend = new FakeDesktopBackend
        {
            ForegroundImpl = _ => throw new DesktopFault(ActStatus.Blocked, ReasonCodes.UserActive, "A modifier is active."),
        };
        backend.LastForegroundAttempt = (false, false);
        await using var engine = backend.NewEngine();

        var attempt = await engine.BringToForegroundDetailedAsync(0x1234, TestContext.Current.CancellationToken);

        Assert.False(attempt.Verified);
        Assert.False(attempt.InputSent);
        Assert.False(attempt.KeyTapped);
        Assert.Equal(ActStatus.Blocked, attempt.StopStatus);
        Assert.Equal(ReasonCodes.UserActive, attempt.StopReason);
        Assert.Contains("modifier", attempt.StopDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_focus_with_the_stop_key_pressed_is_denied_kill_switch_before_any_attempt()
    {
        // Reviewer, round 24: the early refusal carries its reason (denied kill_switch) and never reaches the window.
        var attempted = false;
        var backend = new FakeDesktopBackend { ForegroundImpl = _ => attempted = true };
        await using var engine = backend.NewEngine(kill: new TrippedKill());

        var attempt = await engine.BringToForegroundDetailedAsync(0x1234, TestContext.Current.CancellationToken);

        Assert.False(attempted);
        Assert.False(attempt.Verified);
        Assert.False(attempt.InputSent);
        Assert.False(attempt.KeyTapped);
        Assert.Equal(ActStatus.Denied, attempt.StopStatus);
        Assert.Equal(ReasonCodes.KillSwitch, attempt.StopReason);
    }

    [Fact]
    public async Task A_boundary_stop_after_a_window_input_reports_input_sent_true_without_the_key_and_with_the_stop()
    {
        var backend = new FakeDesktopBackend
        {
            ForegroundImpl = _ => throw new DesktopFault(ActStatus.Blocked, ReasonCodes.WindowChanged, "The target window changed before activation."),
        };
        backend.LastForegroundAttempt = (true, false);
        await using var engine = backend.NewEngine();

        var attempt = await engine.BringToForegroundDetailedAsync(0x1234, TestContext.Current.CancellationToken);

        Assert.False(attempt.Verified);
        Assert.True(attempt.InputSent);
        Assert.False(attempt.KeyTapped);
        Assert.Equal(ReasonCodes.WindowChanged, attempt.StopReason);
    }

    [Fact]
    public async Task A_boundary_stop_after_the_F24_tap_keeps_the_key_and_the_stop()
    {
        var backend = new FakeDesktopBackend
        {
            ForegroundImpl = _ => throw new DesktopFault(ActStatus.Blocked, ReasonCodes.SecureDesktop, "The input desktop changed."),
        };
        backend.LastForegroundAttempt = (true, true);
        await using var engine = backend.NewEngine();

        var attempt = await engine.BringToForegroundDetailedAsync(0x1234, TestContext.Current.CancellationToken);

        Assert.True(attempt.InputSent);
        Assert.True(attempt.KeyTapped);
        Assert.Equal(ActStatus.Blocked, attempt.StopStatus);
        Assert.Equal(ReasonCodes.SecureDesktop, attempt.StopReason);
    }

    [Fact]
    public async Task A_focus_while_a_type_holds_the_lane_is_blocked_rate_limit_with_nothing_sent()
    {
        var backend = new FakeDesktopBackend();
        backend.Editor.SendMs = 2;
        var reached = new TaskCompletionSource();
        backend.Editor.AfterSend = typed =>
        {
            if (typed >= 20) reached.TrySetResult();
        };
        await using var engine = backend.NewEngine();
        var typing = engine.ActAsync(new DesktopActRequest("e1", 0, "type", new string('w', 100)), TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var attempt = await engine.BringToForegroundDetailedAsync(0x1234, TestContext.Current.CancellationToken);

        Assert.False(typing.IsCompleted);
        Assert.False(attempt.Verified);
        Assert.False(attempt.InputSent);
        Assert.False(attempt.KeyTapped);
        Assert.Equal(ActStatus.Blocked, attempt.StopStatus);
        Assert.Equal(ReasonCodes.RateLimit, attempt.StopReason);
        Assert.Contains("type", attempt.StopDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ActStatus.Confirmed, (await typing).Status);
    }

    [Fact]
    public async Task BringToForegroundAsync_is_true_only_when_the_window_was_verified()
    {
        var backend = new FakeDesktopBackend { ForegroundImpl = _ => false };
        await using var engine = backend.NewEngine();

        Assert.False(await engine.BringToForegroundAsync(0x1234, TestContext.Current.CancellationToken));
    }

    private static bool Attempt(bool input, bool key, bool activated, bool throwAfter, out bool windowInput, out bool keyTapped)
    {
        windowInput = input;
        keyTapped = key;
        if (throwAfter) throw new DesktopFault(ActStatus.Blocked, ReasonCodes.UserActive, "A modifier is active.");
        return activated;
    }

    [Fact]
    public void A_desktop_act_focus_that_tapped_F24_reports_the_key_channel()
    {
        var progress = new InputProgress();

        var activated = UiaDesktop.RunForegroundAttempt(
            (long hwnd, out bool input, out bool key, Action? boundary) => Attempt(true, true, true, false, out input, out key), 1, progress, null);

        Assert.True(activated);
        Assert.Equal(Channels.KeyF24, progress.Channel);
        Assert.Equal(1, progress.Sent);
    }

    [Fact]
    public void A_desktop_act_focus_that_used_the_window_api_reports_the_api_channel_and_one_that_sent_nothing_reports_none()
    {
        var used = new InputProgress();
        UiaDesktop.RunForegroundAttempt((long hwnd, out bool input, out bool key, Action? boundary) => Attempt(true, false, true, false, out input, out key), 1, used, null);
        Assert.Equal(Channels.Api, used.Channel);

        var untouched = new InputProgress();
        UiaDesktop.RunForegroundAttempt((long hwnd, out bool input, out bool key, Action? boundary) => Attempt(false, false, true, false, out input, out key), 1, untouched, null);
        Assert.Null(untouched.Channel);
        Assert.Equal(0, untouched.Sent);
    }

    [Fact]
    public void A_stop_after_the_F24_tap_still_counts_the_tap_as_sent_input()
    {
        var progress = new InputProgress();

        Assert.Throws<DesktopFault>(() => UiaDesktop.RunForegroundAttempt(
            (long hwnd, out bool input, out bool key, Action? boundary) => Attempt(true, true, false, true, out input, out key), 1, progress, null));

        Assert.Equal(Channels.KeyF24, progress.Channel);
        Assert.Equal(1, progress.Sent);
    }

    [Fact]
    public async Task A_desktop_act_focus_through_the_engine_returns_the_key_f24_channel_and_input_sent()
    {
        var backend = new FakeDesktopBackend
        {
            FocusAttempt = (long hwnd, out bool input, out bool key, Action? boundary) => Attempt(true, true, true, false, out input, out key),
        };
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(new DesktopActRequest("e1", 0, "focus"), TestContext.Current.CancellationToken);

        Assert.Equal(Channels.KeyF24, result.Channel);
        Assert.True(result.InputSent);
    }

    // ---------- A12: the per-character wait ----------

    private static TypingSeams Seams(Func<int, long, CancellationToken, bool> acknowledged, Func<int, string, CancellationToken, bool> resend,
        Func<string, InputReceipt>? send = null, Action<(int, int)?, bool>? awaitScalar = null) => new()
    {
        BeforeBatch = (_, _) => { },
        StopCheck = _ => { },
        CheckSubmission = _ => { },
        Send = send ?? (batch => new InputReceipt(batch.Length, batch.Length)),
        CaretBefore = () => (0, 0),
        AwaitScalar = awaitScalar ?? ((_, _) => { }),
        Acknowledged = acknowledged,
        Resend = resend,
    };

    [Fact]
    public void When_the_caret_moves_each_step_sends_the_next_character_and_the_text_is_done_after_the_last_acknowledgement()
    {
        var sent = new List<string>();
        var progress = new InputProgress();
        var pump = new TypingPump("abc", 1, 0, progress,
            Seams((_, _, _) => true, (_, _, _) => throw new InvalidOperationException("no re-send expected"),
                batch => { sent.Add(batch); return new InputReceipt(1, 1); }));

        Assert.False(pump.Step(CancellationToken.None));   // sends "a"
        Assert.Equal(["a"], sent);
        Assert.False(pump.Step(CancellationToken.None));   // "a" acknowledged, sends "b"
        Assert.Equal(["a", "b"], sent);
        Assert.False(pump.Step(CancellationToken.None));   // sends "c"
        Assert.True(pump.Step(CancellationToken.None));    // "c" acknowledged: finished
        Assert.Equal(["a", "b", "c"], sent);
        Assert.Equal(0, progress.Resends);
        Assert.Equal(3, progress.Sent);
    }

    [Fact]
    public void A_step_never_sends_more_than_one_character()
    {
        var sends = 0;
        var pump = new TypingPump(new string('q', 50), 1, 0, new InputProgress(),
            Seams((_, _, _) => true, (_, _, _) => false, batch => { sends++; return new InputReceipt(1, 1); }));

        for (var i = 1; i <= 10; i++)
        {
            pump.Step(CancellationToken.None);
            Assert.True(sends <= i, $"{sends} sends after {i} steps");
        }
    }

    [Fact]
    public void A_stuck_caret_sends_the_character_once_more_and_the_second_miss_ends_the_act_with_readback_mismatch()
    {
        var sent = new List<string>();
        var progress = new InputProgress();
        var resends = 0;
        var pump = new TypingPump("ab", 1, 0, progress, Seams(
            (_, _, _) => throw new DesktopFault(ActStatus.Blocked, ReasonCodes.ReadbackMismatch, "not acknowledged"),
            (_, scalar, _) => { resends++; sent.Add("re:" + scalar); progress.Accept(new InputReceipt(1, 1), Channels.UnicodeTyping); return true; },
            batch => { sent.Add(batch); return new InputReceipt(1, 1); }));

        Assert.False(pump.Step(CancellationToken.None));   // sends "a"
        Assert.False(pump.Step(CancellationToken.None));   // not acknowledged: "a" is sent again
        Assert.Equal(["a", "re:a"], sent);
        Assert.Equal(1, progress.Resends);

        var error = Assert.Throws<DesktopFault>(() => pump.Step(CancellationToken.None));   // still not taken
        Assert.Equal(ActStatus.Error, error.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, error.Reason);
        Assert.Equal(1, resends);   // never a second re-send of the same character
        Assert.Equal(2, progress.Sent);
    }

    [Fact]
    public void A_character_the_caret_already_took_is_not_sent_again_and_ends_the_act_with_readback_mismatch()
    {
        var progress = new InputProgress();
        var pump = new TypingPump("a", 1, 0, progress, Seams(
            (_, _, _) => throw new DesktopFault(ActStatus.Blocked, ReasonCodes.ReadbackMismatch, "caret is somewhere else"),
            (_, _, _) => false));

        pump.Step(CancellationToken.None);
        var error = Assert.Throws<DesktopFault>(() => pump.Step(CancellationToken.None));

        Assert.Equal(ActStatus.Error, error.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, error.Reason);
        Assert.Equal(0, progress.Resends);
    }

    [Fact]
    public void A_native_edit_waits_for_the_scalar_after_each_character_and_flags_only_the_first()
    {
        var firsts = new List<bool>();
        var pump = new TypingPump("abc", 1, null, new InputProgress(),
            Seams((_, _, _) => true, (_, _, _) => false, awaitScalar: (_, first) => firsts.Add(first)));

        Assert.False(pump.Step(CancellationToken.None));
        Assert.False(pump.Step(CancellationToken.None));
        Assert.True(pump.Step(CancellationToken.None));
        Assert.Equal([true, false, false], firsts);
    }

    // ---------- A12: the seam in AwaitTypedScalar ----------

    [Fact]
    public void AwaitTypedScalar_returns_as_soon_as_the_caret_moves()
    {
        var reads = 0;
        var sleeps = 0;

        NativeDesktop.AwaitTypedScalar((0, 0), false, 1000, () => { }, () => ++reads >= 3 ? (1, 1) : (0, 0), _ => sleeps++);

        Assert.Equal(3, reads);
        Assert.Equal(2, sleeps);
    }

    [Fact]
    public void AwaitTypedScalar_gives_up_quietly_after_its_time_when_the_caret_is_stuck()
    {
        var sleeps = 0;
        var start = System.Diagnostics.Stopwatch.StartNew();

        // The pause is timed here, not by Thread.Sleep: Windows rounds a 2 ms sleep up to its timer tick (about 15 ms, more under
        // load), which made "more than 3 pauses in 60 ms" depend on how busy the machine was.
        NativeDesktop.AwaitTypedScalar((0, 0), false, 60, () => { }, () => (0, 0), ms =>
        {
            sleeps++;
            var pause = System.Diagnostics.Stopwatch.StartNew();
            while (pause.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(20);
        });

        Assert.InRange(start.ElapsedMilliseconds, 50, 1000);
        Assert.True(sleeps > 3);
    }

    [Fact]
    public void AwaitTypedScalar_for_an_editor_with_no_readable_caret_settles_only_the_first_character()
    {
        var sleeps = new List<int>();
        NativeDesktop.AwaitTypedScalar(null, true, 1000, () => { }, () => null, sleeps.Add);
        NativeDesktop.AwaitTypedScalar(null, false, 1000, () => { }, () => null, sleeps.Add);

        Assert.Equal([120], sleeps);
    }
}
