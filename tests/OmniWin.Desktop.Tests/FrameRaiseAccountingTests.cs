using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

public sealed class FrameRaiseAccountingTests
{
    [Fact]
    public void Successful_no_op_below_topmost_windows_does_not_record_input()
    {
        var order = Complete(99, 10, 20);
        var input = false;
        var captures = 0;
        var raises = 0;

        var succeeded = NativeDesktop.RaiseFrame(10, _ => { captures++; return order; },
            _ => { raises++; return true; }, ref input);

        Assert.True(succeeded);
        Assert.Equal(2, captures);
        Assert.Equal(1, raises);
        Assert.False(input);
    }

    [Fact]
    public void Owned_group_movement_is_recorded_even_when_target_predecessor_is_unchanged()
    {
        // Popup 11 stays immediately above owner 10 while both move above frame 30.
        var snapshots = new Queue<NativeDesktop.ZOrderSnapshot>([Complete(30, 11, 10, 20), Complete(11, 10, 30, 20)]);
        var input = false;

        Assert.True(NativeDesktop.RaiseFrame(10, _ => snapshots.Dequeue(), _ => true, ref input));

        Assert.True(input);
    }

    [Fact]
    public void No_op_raise_preserves_an_earlier_restore_or_activation()
    {
        var order = Complete(11, 10, 20);
        var input = true;

        Assert.True(NativeDesktop.RaiseFrame(10, _ => order, _ => true, ref input));

        Assert.True(input);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Incomplete_order_cannot_prove_a_no_op(bool beforeComplete, bool afterComplete)
    {
        var snapshots = new Queue<NativeDesktop.ZOrderSnapshot>([
            new([10, 20], beforeComplete), new([10, 20], afterComplete)]);
        var input = false;

        Assert.True(NativeDesktop.RaiseFrame(10, _ => snapshots.Dequeue(), _ => true, ref input));

        Assert.True(input);
    }

    [Fact]
    public void Concurrent_order_change_is_conservatively_recorded()
    {
        var snapshots = new Queue<NativeDesktop.ZOrderSnapshot>([Complete(10, 20), Complete(10, 20, 30)]);
        var input = false;

        Assert.True(NativeDesktop.RaiseFrame(10, _ => snapshots.Dequeue(), _ => true, ref input));

        Assert.True(input);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_raise_records_only_observed_or_possible_change(bool orderChanged)
    {
        var snapshots = new Queue<NativeDesktop.ZOrderSnapshot>([
            Complete(20, 10), orderChanged ? Complete(10, 20) : Complete(20, 10)]);
        var input = false;

        Assert.False(NativeDesktop.RaiseFrame(10, _ => snapshots.Dequeue(), _ => false, ref input));

        Assert.Equal(orderChanged, input);
    }

    [Fact]
    public void Stop_during_capture_prevents_the_native_raise()
    {
        var boundaries = 0;
        var raises = 0;
        var input = false;
        var stopped = new OperationCanceledException("stopped after read-only capture");

        var error = Record.Exception(() => NativeDesktop.RaiseFrame(10, _ => Complete(10, 20),
            _ => { raises++; return true; }, ref input, () =>
            {
                if (++boundaries == 2) throw stopped;
            }));

        Assert.Same(stopped, error);
        Assert.Equal(0, raises);
        Assert.False(input);
    }

    [Fact]
    public void Failure_after_native_call_keeps_possible_input_recorded()
    {
        var captures = 0;
        var input = false;

        Assert.Throws<InvalidOperationException>(() => NativeDesktop.RaiseFrame(10,
            _ => ++captures == 1 ? Complete(20, 10) : throw new InvalidOperationException("snapshot unavailable"),
            _ => true, ref input));

        Assert.True(input);
    }

    [Theory]
    [InlineData(ActStatus.Denied, ReasonCodes.PolicyForbidden)]
    [InlineData(ActStatus.Denied, ReasonCodes.KillSwitch)]
    [InlineData(ActStatus.Blocked, ReasonCodes.UserActive)]
    public void Refusal_after_no_op_raise_preserves_status_and_input_sent_false(ActStatus status, string reason)
    {
        var input = false;
        Assert.True(NativeDesktop.RaiseFrame(10, _ => Complete(11, 10, 20), _ => true, ref input));
        var progress = new InputProgress();
        if (input) progress.Accept(new InputReceipt(1, 1), Channels.Api);

        // ExecuteAsync uses this same final-result factory and normalization.
        var result = DesktopEngine.CreateResult(status, progress, Timing.Total(0), reason);

        Assert.Equal(status, result.Status);
        Assert.False(result.InputSent);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData(ActStatus.Denied)]
    [InlineData(ActStatus.Blocked)]
    public void Refusal_after_actual_group_movement_remains_interrupted(ActStatus status)
    {
        var snapshots = new Queue<NativeDesktop.ZOrderSnapshot>([Complete(20, 11, 10), Complete(11, 10, 20)]);
        var input = false;
        Assert.True(NativeDesktop.RaiseFrame(10, _ => snapshots.Dequeue(), _ => true, ref input));
        var progress = new InputProgress();
        if (input) progress.Accept(new InputReceipt(1, 1), Channels.Api);

        var result = DesktopEngine.CreateResult(status, progress, Timing.Total(0), ReasonCodes.KillSwitch);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(ReasonCodes.InputInterrupted, result.Reason);
        Assert.Equal(Channels.Api, result.Channel);
    }

    [Fact]
    public void Cyclic_native_order_is_bounded_and_incomplete()
    {
        var snapshot = NativeDesktop.CaptureZOrder(10, _ => 10, hwnd => hwnd == 10 ? 20 : 10, _ => true);

        Assert.False(snapshot.Complete);
        Assert.Equal(new[] { 10L, 20L }, snapshot.Windows);
    }

    [Fact]
    public void Missing_target_and_exhausted_order_budget_are_incomplete()
    {
        var missing = NativeDesktop.CaptureZOrder(10, _ => 20, _ => 0, _ => true);
        var bounded = NativeDesktop.CaptureZOrder(10, _ => 10, hwnd => hwnd + 1, _ => true, maximum: 2);

        Assert.False(missing.Complete);
        Assert.False(bounded.Complete);
        Assert.Equal(new[] { 10L, 11L }, bounded.Windows);
    }

    private static NativeDesktop.ZOrderSnapshot Complete(params long[] windows) => new(windows, true);
}
