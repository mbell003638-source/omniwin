using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

public sealed class PacketEditorProgressTests
{
    [Fact]
    public void Successful_query_without_caret_progress_is_not_an_acknowledgement()
    {
        var selections = new Queue<(int Start, int End)?>([(4, 4), (4, 4), (5, 5)]);
        var queries = 0;
        var boundaries = 0;
        bool Poll() => PacketEditorProgress.TryAcknowledge(5,
            () => { queries++; return selections.Dequeue(); }, () => boundaries++, () => false);

        Assert.False(Poll());
        Assert.False(Poll());
        Assert.True(Poll());

        Assert.Equal(3, queries);
        Assert.Equal(3, boundaries);
    }

    [Fact]
    public void Both_selection_endpoints_must_reach_the_exact_collapsed_caret()
    {
        var selections = new Queue<(int Start, int End)?>([(5, 6), (4, 5), (5, 5)]);
        bool Poll() => PacketEditorProgress.TryAcknowledge(5, () => selections.Dequeue(), () => { }, () => false);

        Assert.False(Poll());
        Assert.False(Poll());
        Assert.True(Poll());

        Assert.Empty(selections);
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(5, 6)]
    [InlineData(4, 5)]
    public void Overshoot_or_noncollapsed_selection_cannot_acknowledge_the_scalar(int start, int end)
    {
        var fault = Assert.Throws<DesktopFault>(() => PacketEditorProgress.TryAcknowledge(5, () => (start, end),
            () => { }, () => true));

        Assert.Equal(ActStatus.Blocked, fault.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, fault.Reason);
    }

    [Fact]
    public void Unsupported_selection_stops_at_the_deadline()
    {
        var queries = 0;
        var deadlineChecks = 0;
        bool Poll() => PacketEditorProgress.TryAcknowledge(1,
            () => { queries++; return null; }, () => { }, () => ++deadlineChecks >= 3);

        Assert.False(Poll());
        Assert.False(Poll());
        var fault = Assert.Throws<DesktopFault>(() => Poll());

        Assert.Equal(ReasonCodes.ReadbackMismatch, fault.Reason);
        Assert.Equal(3, queries);
    }

    [Fact]
    public void Cancellation_at_a_boundary_prevents_further_selection_queries()
    {
        var boundaries = 0;
        var queries = 0;
        var stopped = new OperationCanceledException("stop between progress reads");
        bool Poll() => PacketEditorProgress.TryAcknowledge(1,
            () => { queries++; return (0, 0); },
            () => { if (++boundaries == 2) throw stopped; }, () => false);

        Assert.False(Poll());
        var error = Record.Exception(() => Poll());

        Assert.Same(stopped, error);
        Assert.Equal(1, queries);
    }

    [Fact]
    public void Selection_provider_failure_propagates_without_acknowledging_progress()
    {
        var queries = 0;
        var providerFailure = new InvalidOperationException("provider unavailable");

        bool Poll() => PacketEditorProgress.TryAcknowledge(1,
            () => ++queries == 1 ? (0, 0) : throw providerFailure, () => { }, () => false);

        Assert.False(Poll());
        var error = Record.Exception(() => Poll());

        Assert.Same(providerFailure, error);
        Assert.Equal(2, queries);
    }

    [Fact]
    public void Missing_acknowledgement_preserves_completed_input_in_the_production_result()
    {
        var progress = new InputProgress();
        progress.Accept(new InputReceipt(2, 2), Channels.UnicodeTyping);
        var fault = Assert.Throws<DesktopFault>(() => PacketEditorProgress.TryAcknowledge(1, () => null,
            () => { }, () => true));

        var result = DesktopEngine.CreateResult(fault.Status, progress, Timing.Total(0), fault.Reason, fault.Message);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.InputInterrupted, result.Reason);
        Assert.True(result.InputSent);
        Assert.Contains("no further typing was sent", result.Detail ?? string.Empty, StringComparison.Ordinal);
    }
}
