using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

/// <summary>
/// Contract 2.5: desktop_act hover and drag. What the engine accepts before anything is sent (fake backend, no input): hover takes
/// no value; drag needs the drop target's element ref as its value. The pointer checks themselves are native and are exercised in
/// the live session.
/// </summary>
public sealed class DragHoverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hover_with_a_value_is_refused_before_anything_is_sent()
    {
        var backend = new FakeDesktopBackend();
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(new DesktopActRequest("e1", 0, "hover", "oops"), Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("w3")]          // a window, not an element
    [InlineData("o4")]          // an OCR word cannot be a drop target
    [InlineData("e")]
    [InlineData("e1x")]
    [InlineData("e30; click")]
    public async Task Drag_without_a_valid_drop_target_ref_is_refused_before_anything_is_sent(string? dropRef)
    {
        var backend = new FakeDesktopBackend();
        await using var engine = backend.NewEngine();

        var result = await engine.ActAsync(new DesktopActRequest("e1", 0, "drag", dropRef), Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
    }

    [Fact]
    public async Task Hover_and_drag_with_valid_arguments_reach_the_backend()
    {
        var backend = new FakeDesktopBackend();
        await using var engine = backend.NewEngine();

        var hover = await engine.ActAsync(new DesktopActRequest("e1", 0, "hover"), Ct);
        var drag = await engine.ActAsync(new DesktopActRequest("e1", 0, "drag", "e30"), Ct);

        Assert.NotEqual(ReasonCodes.UnsupportedAction, hover.Reason);
        Assert.NotEqual(ReasonCodes.UnsupportedAction, drag.Reason);
        Assert.True(hover.InputSent);
        Assert.True(drag.InputSent);
    }

    [Fact]
    public async Task A_drag_while_a_type_holds_the_lane_is_blocked_at_once()
    {
        var backend = new FakeDesktopBackend();
        backend.Editor.SendMs = 2;
        var reached = new TaskCompletionSource();
        backend.Editor.AfterSend = typed =>
        {
            if (typed >= 20) reached.TrySetResult();
        };
        await using var engine = backend.NewEngine();
        var typing = engine.ActAsync(new DesktopActRequest("e1", 0, "type", new string('d', 100)), Ct);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var drag = await engine.ActAsync(new DesktopActRequest("e1", 0, "drag", "e30"), Ct);

        Assert.Equal(ActStatus.Blocked, drag.Status);
        Assert.False(drag.InputSent);
        Assert.Equal(ActStatus.Confirmed, (await typing).Status);
    }

    [Fact]
    public async Task An_engine_call_holding_the_input_lane_blocks_acts_with_a_detail_that_names_it()
    {
        var backend = new FakeDesktopBackend();
        await using var engine = backend.NewEngine();
        using var held = await ((IInputLane)engine).TryEnterAsync("engine", Ct);
        Assert.NotNull(held);

        var click = await engine.ActAsync(new DesktopActRequest("e1", 0, "click"), Ct);
        var second = await ((IInputLane)engine).TryEnterAsync("engine", Ct);

        Assert.Equal(ActStatus.Blocked, click.Status);
        Assert.Equal(ReasonCodes.RateLimit, click.Reason);
        Assert.False(click.InputSent);
        Assert.Contains("engine", click.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(second);   // a second holder is refused, not queued
    }
}
