using System.Runtime.InteropServices;
using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

public sealed class InputContractTests
{
    [Theory]
    [InlineData("invoke", false)]
    [InlineData("set_value", false)]
    [InlineData("clear", false)]
    [InlineData("toggle", false)]
    [InlineData("select", false)]
    [InlineData("expand", false)]
    [InlineData("collapse", false)]
    [InlineData("scroll_into_view", false)]
    [InlineData("focus", true)]
    [InlineData("type", true)]
    [InlineData("click", true)]
    [InlineData("double_click", true)]
    [InlineData("right_click", true)]
    public void Explicit_UIA_patterns_use_addressed_background_operations(string action, bool sharedInput)
    {
        Assert.Equal(sharedInput, new DesktopCommand("desktop_act", action).UsesSharedInput);
    }

    [Theory]
    [InlineData("desktop_keys", "keys")]
    [InlineData("desktop_scroll", "down")]
    public void Physical_keys_and_wheel_scroll_use_shared_input(string tool, string action)
    {
        Assert.True(new DesktopCommand(tool, action).UsesSharedInput);
    }

    [Theory]
    [InlineData("CTRL+S", "ctrl+s")]
    [InlineData("shift+control+Del", "ctrl+shift+delete")]
    [InlineData("alt+ctrl+shift+k", "ctrl+alt+shift+k")]
    [InlineData("WIN+R", "win+r")]
    [InlineData("Esc Return PgUp PgDn Ins BS", "escape enter pageup pagedown insert backspace")]
    [InlineData("ctrl+a  delete", "ctrl+a delete")]
    [InlineData("  f1\tf24\nspace  ", "f1 f24 space")]
    public void Key_sequences_are_canonicalized_before_the_safety_gate(string input, string expected)
    {
        var sequence = KeySequence.Parse(input);

        Assert.NotNull(sequence);
        Assert.Equal(expected, sequence.Canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ctrl")]
    [InlineData("ctrl+")]
    [InlineData("+a")]
    [InlineData("ctrl++a")]
    [InlineData("ctrl+control+a")]
    [InlineData("a+ctrl")]
    [InlineData("meta+a")]
    [InlineData("f0")]
    [InlineData("f25")]
    [InlineData("unknown")]
    [InlineData("ctrl+;")]
    [InlineData("ctrl+a invalid")]
    public void Invalid_keys_fail_as_a_whole_before_any_input(string? input)
    {
        Assert.Null(KeySequence.Parse(input));
    }

    [Fact]
    public void Overlong_key_sequences_are_rejected()
    {
        Assert.NotNull(KeySequence.Parse(string.Join(' ', Enumerable.Repeat("tab", 64))));
        Assert.Null(KeySequence.Parse(string.Join(' ', Enumerable.Repeat("tab", 65))));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1919, 1920)]
    [InlineData(3839, 3840)]
    [InlineData(65535, 65536)]
    public void Absolute_mouse_coordinates_map_back_to_the_requested_pixel(int offset, int extent)
    {
        var normalized = NativeInput.Normalize(offset, extent);

        Assert.InRange(normalized, 0, 65535);
        Assert.Equal(offset, (int)((long)normalized * extent / 65536));
    }

    [Fact]
    public void Partial_native_input_is_reported_as_interrupted_and_preserves_the_channel()
    {
        var progress = new InputProgress();
        progress.Accept(new InputReceipt(2, 2), Channels.UnicodeTyping);

        var fault = Assert.Throws<DesktopFault>(() => progress.Accept(new InputReceipt(1, 4), Channels.UnicodeTyping));

        Assert.Equal(3, progress.Sent);
        Assert.Equal(Channels.UnicodeTyping, progress.Channel);
        Assert.Equal(ActStatus.Error, fault.Status);
        Assert.Equal(ReasonCodes.InputInterrupted, fault.Reason);
    }

    [Fact]
    public void Rejected_native_input_keeps_input_sent_false()
    {
        var progress = new InputProgress();

        var fault = Assert.Throws<DesktopFault>(() => progress.Accept(new InputReceipt(0, 4), Channels.Keys));

        Assert.Equal(0, progress.Sent);
        Assert.Null(progress.Channel);
        Assert.Equal(ActStatus.Error, fault.Status);
        Assert.Equal(ReasonCodes.UnsupportedAction, fault.Reason);
    }

    [Fact]
    public void Successful_provider_dispatch_counts_as_input()
    {
        var progress = new InputProgress();
        var invoked = false;

        progress.Pattern(() => invoked = true, Channels.Invoke);

        Assert.True(invoked);
        Assert.Equal(1, progress.Sent);
        Assert.Equal(Channels.Invoke, progress.Channel);
    }

    [Theory]
    [InlineData(unchecked((int)0x80040201))]
    [InlineData(unchecked((int)0x80040200))]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x80004001))]
    [InlineData(unchecked((int)0x80040204))]
    [InlineData(unchecked((int)0x800705B4))]
    [InlineData(unchecked((int)0x8001011F))]
    [InlineData(unchecked((int)0x80004005))]
    public void Provider_dispatch_failures_preserve_possible_delivery_to_prevent_retries(int hresult)
    {
        var progress = new InputProgress();

        Assert.Throws<COMException>(() => progress.Pattern(() => throw new COMException("Ambiguous", hresult), Channels.Invoke));

        Assert.Equal(1, progress.Sent);
        Assert.Equal(Channels.Invoke, progress.Channel);
    }
}
