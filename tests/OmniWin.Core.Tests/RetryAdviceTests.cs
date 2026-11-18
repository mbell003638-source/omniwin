namespace OmniWin.Core.Tests;

/// <summary>Contract 2.5: the retry= word on every action result line, derived in one place.</summary>
public sealed class RetryAdviceTests
{
    [Theory]
    [InlineData(ActStatus.Confirmed, true, null, "done")]
    [InlineData(ActStatus.Confirmed, false, null, "done")]
    // input WAS sent and the result is not confirmed: never repeat blindly, whatever the status or reason
    [InlineData(ActStatus.Unverified, true, null, "check_state")]
    [InlineData(ActStatus.Contradicted, true, ReasonCodes.ReadbackMismatch, "check_state")]
    [InlineData(ActStatus.Timeout, true, null, "check_state")]
    [InlineData(ActStatus.Error, true, ReasonCodes.InputInterrupted, "check_state")]
    [InlineData(ActStatus.Error, true, ReasonCodes.EngineTimeout, "check_state")]
    [InlineData(ActStatus.Error, true, ReasonCodes.ReadbackMismatch, "check_state")]
    // nothing sent, target changed or not ready
    [InlineData(ActStatus.Stale, false, ReasonCodes.NotFound, "observe_first")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.ModalBlocking, "observe_first")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.OccludedByWindow, "observe_first")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.Offscreen, "observe_first")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.FocusFailed, "observe_first")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.WindowChanged, "observe_first")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.Disabled, "observe_first")]
    // nothing sent because something else is busy
    [InlineData(ActStatus.Blocked, false, ReasonCodes.UserActive, "wait")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.RateLimit, "wait")]
    // repeating cannot work
    [InlineData(ActStatus.Denied, false, ReasonCodes.PolicyForbidden, "no")]
    [InlineData(ActStatus.Denied, false, ReasonCodes.KillSwitch, "no")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.UnsupportedAction, "no")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.ElevatedTarget, "no")]
    [InlineData(ActStatus.Blocked, false, ReasonCodes.SecureDesktop, "no")]
    // an error before anything was sent: the same call may be repeated
    [InlineData(ActStatus.Error, false, ReasonCodes.EngineTimeout, "safe")]
    [InlineData(ActStatus.Error, false, null, "safe")]
    public void The_word_follows_status_input_sent_and_reason(ActStatus status, bool inputSent, string? reason, string expected) =>
        Assert.Equal(expected, RetryAdvice.For(status, inputSent, reason));

    [Fact]
    public void Every_action_result_line_carries_retry_before_the_timing()
    {
        var line = ResultFormatter.Format(new ActResult(ActStatus.Unverified, true, null, null, null, Channels.Api, Timing.Total(3)));

        Assert.Equal("status=unverified input_sent=true channel=api retry=check_state total=3.0ms", line);
    }

    [Fact]
    public void The_word_comes_after_the_risk_label_and_the_reason()
    {
        var line = ResultFormatter.Format(new ActResult(ActStatus.Blocked, false, ReasonCodes.UserActive, "paused", null, null, Timing.Total(1),
            Risk: RiskClass.Risky));

        Assert.StartsWith("status=blocked input_sent=false reason=user_active risk=risky retry=wait total=", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blocked_result_that_ends_up_with_input_sent_true_is_never_called_safe()
    {
        // The formatter must not offer "safe" once input may have been sent, whatever the status says.
        Assert.Equal("check_state", RetryAdvice.For(ActStatus.Blocked, true, ReasonCodes.UserActive));
    }

    [Fact]
    public void All_lists_every_value_once()
    {
        Assert.Equal(6, RetryAdvice.All.Count);
        Assert.Equal(RetryAdvice.All.Count, RetryAdvice.All.Distinct().Count());
    }
}
