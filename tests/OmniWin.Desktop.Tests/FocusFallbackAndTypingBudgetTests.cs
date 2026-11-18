namespace OmniWin.Desktop.Tests;

/// <summary>
/// File 22, A3, A5 and A6: the typing time limit grows with the text and has a hard cap; the two focus fallbacks (input-queue
/// attach, F24 tap) are skipped for a hung window, and the tap is skipped for clients that forward keys to another computer.
/// Fakes only: no window is touched and no input is sent. In the "Desktop input" collection because the two seams are static.
/// </summary>
[Collection("Desktop input")]
public sealed class FocusFallbackAndTypingBudgetTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(4);

    [Fact]
    public void The_typing_limit_is_the_normal_limit_plus_60_ms_per_character()
    {
        Assert.Equal(Base, DesktopEngine.TypingBudget(Base, 0));
        Assert.Equal(Base + TimeSpan.FromMilliseconds(60 * 100), DesktopEngine.TypingBudget(Base, 100));
        Assert.Equal(Base + TimeSpan.FromSeconds(60), DesktopEngine.TypingBudget(Base, 1000));
    }

    [Fact]
    public void Two_thousand_characters_get_enough_time_and_the_limit_never_passes_the_cap()
    {
        // 2,000 characters at 60 ms each would be 124 s: capped at 120 s, which still allows 60 ms for every character.
        Assert.Equal(DesktopEngine.TypingBudgetCap, DesktopEngine.TypingBudget(Base, 2000));
        Assert.Equal(DesktopEngine.TypingBudgetCap, DesktopEngine.TypingBudget(Base, 1_000_000));
        Assert.True(DesktopEngine.TypingBudget(Base, 2000) >= TimeSpan.FromMilliseconds(2000 * 50));
    }

    [Fact]
    public void A_negative_length_or_a_configured_limit_above_the_cap_never_shortens_the_limit()
    {
        Assert.Equal(Base, DesktopEngine.TypingBudget(Base, -5));
        var large = TimeSpan.FromSeconds(300);
        Assert.Equal(large, DesktopEngine.TypingBudget(large, 10));
    }

    [Theory]
    [InlineData("mstsc")]
    [InlineData("mstsc.exe")]
    [InlineData("MSRDC.EXE")]
    [InlineData("vmconnect")]
    [InlineData("VirtualBoxVM")]
    [InlineData("vmware")]
    [InlineData("vmware-vmx.exe")]
    public void Remote_desktop_and_virtual_machine_clients_are_recognised(string processName) =>
        Assert.True(NativeDesktop.IsForwardingClient(processName));

    [Theory]
    [InlineData("notepad")]
    [InlineData("explorer.exe")]
    [InlineData("vmware-tray")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_processes_are_not_treated_as_forwarding_clients(string? processName) =>
        Assert.False(NativeDesktop.IsForwardingClient(processName));

    private static (bool Attach, bool Tap) Allowed(bool responds, string? processName, long foreground = 1234)
    {
        var (oldResponds, oldName) = (NativeDesktop.Responds, NativeDesktop.ProcessNameOf);
        try
        {
            NativeDesktop.Responds = _ => responds;
            NativeDesktop.ProcessNameOf = _ => processName;
            return NativeDesktop.FocusFallbacksAllowed(foreground);
        }
        finally
        {
            (NativeDesktop.Responds, NativeDesktop.ProcessNameOf) = (oldResponds, oldName);
        }
    }

    [Fact]
    public void A_hung_window_in_front_gets_neither_the_attach_nor_the_key_tap() =>
        Assert.Equal((false, false), Allowed(responds: false, processName: "notepad"));

    [Fact]
    public void A_hung_remote_desktop_client_gets_neither_fallback() =>
        Assert.Equal((false, false), Allowed(responds: false, processName: "mstsc"));

    [Theory]
    [InlineData("mstsc")]
    [InlineData("vmconnect")]
    public void A_responding_forwarding_client_allows_the_attach_but_never_the_key_tap(string processName) =>
        Assert.Equal((true, false), Allowed(responds: true, processName: processName));

    [Fact]
    public void A_responding_ordinary_window_allows_both_fallbacks() =>
        Assert.Equal((true, true), Allowed(responds: true, processName: "notepad"));

    [Fact]
    public void An_unknown_process_name_still_allows_the_tap_because_only_named_clients_forward_keys() =>
        Assert.Equal((true, true), Allowed(responds: true, processName: null));

    [Fact]
    public void With_no_window_in_front_there_is_no_thread_to_attach_to()
    {
        var asked = false;
        var oldResponds = NativeDesktop.Responds;
        try
        {
            NativeDesktop.Responds = _ => { asked = true; return true; };
            Assert.Equal((false, true), NativeDesktop.FocusFallbacksAllowed(0));
            Assert.False(asked);
        }
        finally
        {
            NativeDesktop.Responds = oldResponds;
        }
    }
}
