using OmniWin.Core;

namespace OmniWin.Safety.Tests;

/// <summary>
/// F1: the gate refuses a paste that the engine flagged as a delete command, and a paste into an ordinary field is unchanged.
/// The engine sets <c>ContainsDeleteCommand</c> from the clipboard text; here the flag is set directly. Descriptors only,
/// nothing is run.
/// </summary>
public sealed class PasteIntoCommandWindowGateTests
{
    private static string? Forbidden(ActionDescriptor action) =>
        DeletePolicy.WhyForbidden(action, DeletePolicy.DefaultBlockedProcesses, []);

    private static ElementDescriptor Terminal() =>
        new("e1", "desktop", "WindowsTerminal.exe", "Windows Terminal", null, "terminal", "Terminal", null, false, true, []);

    private static ElementDescriptor TextBox() =>
        new("e2", "desktop", "notepad.exe", "Untitled - Notepad", null, "edit", "Text editor", null, false, true, []);

    [Theory]
    [InlineData("ctrl+v")]
    [InlineData("shift+insert")]
    [InlineData("ctrl+shift+v")]
    public void A_flagged_paste_key_into_a_terminal_is_denied(string keys)
    {
        var action = new ActionDescriptor("desktop_keys", "desktop", "keys", Terminal(), "WindowsTerminal.exe", "Windows Terminal", null, null, keys)
        {
            ContainsDeleteCommand = true,
        };
        Assert.NotNull(Forbidden(action));
    }

    [Theory]
    [InlineData("right_click")]
    [InlineData("click")]
    [InlineData("invoke")]
    public void A_flagged_right_click_paste_or_paste_menu_item_is_denied(string verb)
    {
        var action = new ActionDescriptor("desktop_act", "desktop", verb, Terminal(), "WindowsTerminal.exe", "Windows Terminal", null, null, null)
        {
            ContainsDeleteCommand = true,
        };
        Assert.NotNull(Forbidden(action));
    }

    [Theory]
    [InlineData("ctrl+v")]
    [InlineData("shift+insert")]
    public void An_unflagged_harmless_paste_into_a_terminal_is_allowed(string keys)
    {
        var action = new ActionDescriptor("desktop_keys", "desktop", "keys", Terminal(), "WindowsTerminal.exe", "Windows Terminal", null, null, keys);
        Assert.Null(Forbidden(action));
    }

    [Theory]
    [InlineData("ctrl+v")]
    [InlineData("shift+insert")]
    public void A_paste_into_an_ordinary_text_box_is_allowed_and_the_box_is_not_a_command_target(string keys)
    {
        var action = new ActionDescriptor("desktop_keys", "desktop", "keys", TextBox(), "notepad.exe", "Untitled - Notepad", null, null, keys);
        Assert.Null(Forbidden(action));
        Assert.False(DeletePolicy.IsCommandTarget(TextBox(), "notepad.exe", "Untitled - Notepad"));
        Assert.True(DeletePolicy.IsCommandTarget(Terminal(), "WindowsTerminal.exe", "Windows Terminal"));
    }
}
