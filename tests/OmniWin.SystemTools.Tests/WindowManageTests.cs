using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

public sealed class WindowManageTests
{
    private static Harness WithWindow(out WindowInfo window)
    {
        var h = new Harness();
        window = h.AddWindow("w1", 1001, "fixture.exe", "Fixture window");
        return h;
    }

    [Theory]
    [InlineData("explode", null, null, null, null)]
    [InlineData("move", null, 5, null, null)]
    [InlineData("move", 5, null, null, null)]
    [InlineData("resize", null, null, null, 300)]
    [InlineData("resize", null, null, 300, null)]
    [InlineData("resize", null, null, 0, 300)]
    [InlineData("resize", null, null, 40000, 300)]
    public async Task Bad_arguments_return_a_non_success_status_and_send_nothing(string action, int? x, int? y, int? width, int? height)
    {
        var h = WithWindow(out _);

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", action, x, y, width, height), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task A_missing_window_argument_is_refused()
    {
        var h = WithWindow(out _);

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("", "minimize"), Harness.Ct);

        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
    }

    [Theory]
    [InlineData("w1")]
    [InlineData("fixture")]
    [InlineData("fixture.exe")]
    [InlineData("Fixture win")]
    public async Task The_window_argument_accepts_an_id_a_process_or_a_title_part(string selector)
    {
        var h = WithWindow(out _);

        var result = await h.Service.WindowManageAsync(new WindowManageRequest(selector, "minimize"), Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(h.Windows.Read(1001).Minimized);
    }

    [Fact]
    public async Task An_unknown_or_ambiguous_window_is_blocked_not_found_and_nothing_is_sent()
    {
        var h = WithWindow(out _);
        h.AddWindow("w2", 1002, "fixture.exe", "Fixture window two");

        var unknown = await h.Service.WindowManageAsync(new WindowManageRequest("nothing-like-this", "minimize"), Harness.Ct);
        var ambiguous = await h.Service.WindowManageAsync(new WindowManageRequest("fixture", "minimize"), Harness.Ct);

        Assert.Equal(ReasonCodes.NotFound, unknown.Reason);
        Assert.Equal(ReasonCodes.NotFound, ambiguous.Reason);
        Assert.Contains("w1", ambiguous.Detail);
        Assert.False(unknown.InputSent || ambiguous.InputSent);
        Assert.Empty(h.Log);
    }

    [Theory]
    [InlineData("w5")]
    [InlineData("W5")]
    public async Task A_window_id_that_no_longer_exists_never_falls_back_to_a_title_match(string selector)
    {
        // "Draw50.png" contains "w5": a gone window w5 must not close it.
        var h = new Harness();
        h.AddWindow("w1", 1001, "mspaint.exe", "Draw50.png - Paint");

        var result = await h.Service.WindowManageAsync(new WindowManageRequest(selector, "close"), Harness.Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.NotFound, result.Reason);
        Assert.Contains("no longer exists", result.Detail);
        Assert.False(result.InputSent);
        Assert.Empty(h.Log);
        Assert.True(h.Windows.States.ContainsKey(1001), "the unrelated window must still be open");
    }

    [Theory]
    [InlineData("w12", true)]
    [InlineData("W3", true)]
    [InlineData("w", false)]
    [InlineData("w1a", false)]
    [InlineData("notepad", false)]
    [InlineData("Draw50", false)]
    public void Window_ids_are_recognised_exactly(string selector, bool expected) =>
        Assert.Equal(expected, SystemToolsService.IsWindowId(selector));

    [Fact]
    public async Task The_gate_is_called_with_a_system_descriptor_after_resolve_and_before_any_change()
    {
        var h = WithWindow(out _);

        await h.Service.WindowManageAsync(new WindowManageRequest("w1", "maximize"), Harness.Ct);

        var descriptor = Assert.Single(h.Gate.Seen);
        Assert.Equal("system", descriptor.Engine);
        Assert.Equal("window_manage", descriptor.Tool);
        Assert.Equal("maximize", descriptor.Action);
        Assert.Equal("fixture.exe", descriptor.Process);
        Assert.Equal("Fixture window", descriptor.WindowTitle);
        Assert.Null(descriptor.Target);
        Assert.Equal(["gate", "input:maximize"], h.Log);
    }

    [Fact]
    public async Task A_gate_denial_changes_nothing_and_reports_no_input()
    {
        var h = WithWindow(out _);
        h.Gate.Decide = _ => new GateResult(false, RiskClass.Forbidden, ReasonCodes.PolicyForbidden, false, "no");

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "close"), Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.False(result.InputSent);
        Assert.Equal(RiskClass.Risky, result.Risk);
        Assert.DoesNotContain("input:close", h.Log);
        Assert.True(h.Windows.Read(1001).Exists);
    }

    [Fact]
    public async Task The_stop_key_denies_before_the_gate_and_before_any_change()
    {
        var h = WithWindow(out _);
        h.Kill.IsTripped = true;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "minimize"), Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.KillSwitch, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task A_stop_key_pressed_while_the_gate_runs_still_stops_the_change()
    {
        var h = WithWindow(out _);
        h.Gate.Decide = _ =>
        {
            h.Kill.IsTripped = true;
            return new GateResult(true, RiskClass.Normal, null, false);
        };

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "minimize"), Harness.Ct);

        Assert.Equal(ReasonCodes.KillSwitch, result.Reason);
        Assert.False(result.InputSent);
        Assert.DoesNotContain("input:minimize", h.Log);
    }

    [Fact]
    public async Task The_user_activity_pause_blocks_window_changes()
    {
        var h = WithWindow(out _);
        h.Activity.UserActiveRecently = true;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "minimize"), Harness.Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.UserActive, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task An_elevated_window_is_blocked_elevated_target()
    {
        var h = WithWindow(out _);
        h.AddWindow("w2", 1002, "taskmgr.exe", "Task Manager", inaccessible: "elevated");

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w2", "minimize"), Harness.Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.ElevatedTarget, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Gate.Seen);
    }

    [Theory]
    [InlineData("secure_desktop", ActStatus.Blocked, "secure_desktop")]
    [InlineData("blocked_by_policy", ActStatus.Denied, "policy_forbidden")]
    public async Task Secure_desktop_and_blocklisted_windows_are_refused(string inaccessible, ActStatus status, string reason)
    {
        var h = WithWindow(out _);
        h.AddWindow("w2", 1002, "LogonUI.exe", "Sign in", inaccessible);

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w2", "minimize"), Harness.Ct);

        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.False(result.InputSent);
    }

    [Fact]
    public async Task OmniWins_own_windows_are_forbidden()
    {
        var h = WithWindow(out _);
        h.AddWindow("w2", 1002, "OmniWin.Server.exe", "OmniWin", pid: Environment.ProcessId);

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w2", "close"), Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
        Assert.False(result.InputSent);
        Assert.DoesNotContain("input:close", h.Log);
    }

    [Theory]
    [InlineData("minimize")]
    [InlineData("maximize")]
    [InlineData("restore")]
    public async Task State_changes_are_read_back_and_confirmed(string action)
    {
        var h = WithWindow(out _);
        if (action == "restore") h.Windows.States[1001] = h.Windows.States[1001] with { Minimized = true };

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", action), Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(Channels.Api, result.Channel);
        Assert.Null(result.Risk);
    }

    [Fact]
    public async Task Move_resize_and_snap_are_read_back()
    {
        var h = WithWindow(out _);

        var move = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "move", X: 300, Y: 200), Harness.Ct);
        Assert.Equal(ActStatus.Confirmed, move.Status);
        Assert.Equal(new WindowState(true, false, false, 300, 200, 800, 600), h.Windows.Read(1001));

        var resize = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "resize", Width: 640, Height: 480), Harness.Ct);
        Assert.Equal(ActStatus.Confirmed, resize.Status);
        Assert.Equal(new WindowState(true, false, false, 300, 200, 640, 480), h.Windows.Read(1001));

        var left = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "snap_left"), Harness.Ct);
        Assert.Equal(ActStatus.Confirmed, left.Status);
        Assert.Equal(new WindowState(true, false, false, 0, 0, 960, 1040), h.Windows.Read(1001));

        var right = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "snap_right"), Harness.Ct);
        Assert.Equal(ActStatus.Confirmed, right.Status);
        Assert.Equal(new WindowState(true, false, false, 960, 0, 960, 1040), h.Windows.Read(1001));
    }

    [Fact]
    public async Task Moving_a_maximized_window_restores_it_first()
    {
        var h = WithWindow(out _);
        h.Windows.States[1001] = h.Windows.States[1001] with { Maximized = true };

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "move", X: 50, Y: 60), Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.False(h.Windows.Read(1001).Maximized);
    }

    [Fact]
    public async Task A_change_the_window_ignores_is_contradicted_not_confirmed()
    {
        var h = WithWindow(out _);
        h.Windows.IgnoreRequests = true;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "minimize"), Harness.Ct);

        Assert.Equal(ActStatus.Contradicted, result.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, result.Reason);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task Close_is_labelled_risky_and_confirmed_when_the_window_is_gone()
    {
        var h = WithWindow(out _);

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "close"), Harness.Ct);

        Assert.Equal(RiskClass.Risky, result.Risk);
        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(result.InputSent);
        Assert.Contains(" risk=risky", ResultFormatter.Format(result).Split('\n')[0]);
    }

    [Fact]
    public async Task Close_of_a_window_that_stays_open_is_unverified_not_confirmed()
    {
        var h = WithWindow(out _);
        h.Windows.IgnoreClose = true;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "close"), Harness.Ct);

        Assert.Equal(ActStatus.Unverified, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(RiskClass.Risky, result.Risk);
    }

    [Theory]
    [InlineData(true, ActStatus.Confirmed)]
    [InlineData(false, ActStatus.Error)]
    public async Task Focus_uses_the_foreground_service_and_reports_its_verified_answer(bool focused, ActStatus status)
    {
        var h = WithWindow(out _);
        h.Foreground.Result = focused;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(status, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(["gate", "input:focus"], h.Log);
        if (!focused) Assert.Equal(ReasonCodes.ForegroundFailed, result.Reason);
    }

    [Theory]
    [InlineData(true, ActStatus.Confirmed)]
    [InlineData(false, ActStatus.Error)]
    public async Task Focus_that_tapped_the_F24_key_reports_the_key_channel_not_api(bool focused, ActStatus status)
    {
        var h = WithWindow(out _);
        h.Foreground.Result = focused;
        h.Foreground.KeyTapped = true;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(status, result.Status);
        Assert.Equal(Channels.KeyF24, result.Channel);
        Assert.Equal("key:F24", result.Channel);
        Assert.True(result.InputSent);
        if (!focused) Assert.Equal(ReasonCodes.ForegroundFailed, result.Reason);
    }

    [Fact]
    public async Task Focus_that_failed_without_sending_anything_reports_input_sent_false()
    {
        var h = WithWindow(out _);
        h.Foreground.Result = false;
        h.Foreground.InputSent = false;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.ForegroundFailed, result.Reason);
        Assert.False(result.InputSent);
        Assert.Contains("no input was sent", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Focus_that_sent_input_but_could_not_verify_reports_input_sent_true()
    {
        var h = WithWindow(out _);
        h.Foreground.Result = false;
        h.Foreground.InputSent = true;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task Focus_on_a_window_that_was_already_in_front_is_confirmed_with_input_sent_false()
    {
        var h = WithWindow(out _);
        h.Foreground.Result = true;
        h.Foreground.InputSent = false;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.False(result.InputSent);
        Assert.NotEqual(Channels.KeyF24, result.Channel);
    }

    [Fact]
    public async Task A_foreground_service_that_can_only_answer_yes_or_no_keeps_input_sent_true_when_it_fails()
    {
        var log = new List<string>();
        var h = new Harness(foreground: new YesNoForeground(log) { Result = false });
        h.AddWindow("w1", 1001, "notepad.exe", "Untitled - Notepad");

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task A_focus_call_cancelled_while_the_attempt_ran_reports_that_input_may_have_been_sent()
    {
        var h = WithWindow(out _);
        // The attempt is stopped (its own time limit or a stop key) while it runs: it may already have sent something.
        h.Foreground.Throws = new OperationCanceledException();

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task A_focus_stopped_before_any_input_passes_the_stops_status_and_reason_through_with_input_sent_false()
    {
        var h = WithWindow(out _);
        h.Foreground.Result = false;
        h.Foreground.InputSent = false;
        h.Foreground.StopStatus = ActStatus.Blocked;
        h.Foreground.StopReason = ReasonCodes.UserActive;
        h.Foreground.StopDetail = "A modifier is active.";

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.UserActive, result.Reason);
        Assert.False(result.InputSent);
        Assert.Null(result.Channel);
        Assert.Equal("A modifier is active.", result.Detail);
    }

    [Fact]
    public async Task A_focus_stopped_after_a_window_input_is_error_input_interrupted_with_input_sent_true_on_the_api_channel()
    {
        var h = WithWindow(out _);
        h.Foreground.Result = false;
        h.Foreground.InputSent = true;
        h.Foreground.StopStatus = ActStatus.Blocked;
        h.Foreground.StopReason = ReasonCodes.WindowChanged;
        h.Foreground.StopDetail = "The target window changed before activation.";

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.InputInterrupted, result.Reason);
        Assert.True(result.InputSent);
        Assert.Equal(Channels.Api, result.Channel);
        Assert.Contains(ReasonCodes.WindowChanged, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_focus_stopped_after_the_F24_tap_keeps_the_key_channel()
    {
        var h = WithWindow(out _);
        h.Foreground.Result = false;
        h.Foreground.InputSent = true;
        h.Foreground.KeyTapped = true;
        h.Foreground.StopStatus = ActStatus.Blocked;
        h.Foreground.StopReason = ReasonCodes.SecureDesktop;
        h.Foreground.StopDetail = "The input desktop changed.";

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.InputInterrupted, result.Reason);
        Assert.True(result.InputSent);
        Assert.Equal(Channels.KeyF24, result.Channel);
    }

    [Fact]
    public async Task A_focus_refused_because_a_type_is_running_is_blocked_rate_limit_with_input_sent_false()
    {
        var h = WithWindow(out _);
        h.Foreground.Result = false;
        h.Foreground.InputSent = false;
        h.Foreground.StopStatus = ActStatus.Blocked;
        h.Foreground.StopReason = ReasonCodes.RateLimit;
        h.Foreground.StopDetail = "A type action is running and holds the keyboard and mouse. Try again when it has finished.";

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.RateLimit, result.Reason);
        Assert.False(result.InputSent);
        Assert.Contains("type", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_focus_that_failed_with_no_stop_still_reports_foreground_failed()
    {
        var h = WithWindow(out _);
        h.Foreground.Result = false;

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.ForegroundFailed, result.Reason);
    }

    [Fact]
    public async Task Focus_that_tapped_no_key_does_not_report_the_key_channel()
    {
        var h = WithWindow(out _);

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "focus"), Harness.Ct);

        Assert.NotEqual(Channels.KeyF24, result.Channel);
    }

    [Fact]
    public async Task Phase_timings_use_the_contract_keys_and_exclude_the_gate_from_the_metric()
    {
        var h = WithWindow(out _);

        var result = await h.Service.WindowManageAsync(new WindowManageRequest("w1", "minimize"), Harness.Ct);

        foreach (var phase in new[] { "resolve", "precheck", "gate", "input", "verify" })
        {
            Assert.True(result.Timing.PhasesMs.ContainsKey(phase), phase);
        }

        var metric = Assert.Single(h.Metrics.Records);
        Assert.Equal("window_manage", metric.Op);
        Assert.True(metric.Ms <= result.Timing.TotalMs - result.Timing.PhasesMs["gate"] + 0.001);
    }
}
