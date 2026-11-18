using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

/// <summary>
/// File 22, A1 and A2: a lost character is sent again at most once, and an act that re-sent anything is "confirmed" only when a
/// second readback, taken after the text settled, still matches. Pure decisions; no window is touched.
/// </summary>
public sealed class ResendRulesTests
{
    [Fact]
    public void A_character_is_sent_again_only_once()
    {
        Assert.True(UiaDesktop.MayResend(alreadyResentThisCharacter: false, batchLength: 1));
        Assert.False(UiaDesktop.MayResend(alreadyResentThisCharacter: true, batchLength: 1));
    }

    [Fact]
    public void Nothing_is_sent_again_when_no_character_has_been_sent()
    {
        Assert.False(UiaDesktop.MayResend(false, 0));
    }

    [Fact]
    public void A_matching_settled_readback_keeps_confirmed()
    {
        Assert.Null(DesktopEngine.ResendOutcome(new ConditionCheck(true, "text matched")));
    }

    [Fact]
    public void The_late_original_changing_the_text_after_a_re_send_is_contradicted()
    {
        // The first readback matched; 500 ms later the original character arrived as well, so the text is now wrong.
        var outcome = DesktopEngine.ResendOutcome(new ConditionCheck(false, "text now has a doubled character", Mismatch: true));

        Assert.NotNull(outcome);
        Assert.Equal(ActStatus.Contradicted, outcome.Value.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, outcome.Value.Reason);
        Assert.Contains("sent again", outcome.Value.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_settled_text_that_cannot_be_read_is_unverified_never_confirmed(bool hasCheck)
    {
        var outcome = DesktopEngine.ResendOutcome(hasCheck ? new ConditionCheck(false, "not readable") : null);

        Assert.NotNull(outcome);
        Assert.Equal(ActStatus.Unverified, outcome.Value.Status);
        Assert.Null(outcome.Value.Reason);
    }

    [Fact]
    public void The_settle_wait_is_half_a_second()
    {
        Assert.Equal(500, DesktopEngine.ResendSettleMs);
    }
}
