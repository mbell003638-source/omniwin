using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

/// <summary>
/// F1: a paste into a command window is checked against the clipboard text with the same delete-command check that typed
/// text gets. Fake clipboard text only; no real clipboard, no real input, no real command is ever run.
/// </summary>
[Collection("PasteClipboard")]
public sealed class PasteIntoCommandWindowTests : IDisposable
{
    private const string DeleteText = "del important.txt";
    private const string HarmlessText = "echo hello";
    private readonly Func<ClipboardSnapshot> _original = PasteClipboard.Reader;

    public void Dispose() => PasteClipboard.Reader = _original;

    private static void Clipboard(bool readable, string? text) => PasteClipboard.Reader = () => new ClipboardSnapshot(readable, text);

    private static DesktopCommand Keys(string keys) => new("desktop_keys", "keys", Keys: KeySequence.Parse(keys));

    private static DesktopCommand Act(string action) => new("desktop_act", action, Ref: "e1");

    [Theory]
    [InlineData("ctrl+v")]
    [InlineData("shift+insert")]
    [InlineData("ctrl+shift+v")]
    public void Paste_keys_are_denied_when_the_clipboard_holds_a_delete_command(string keys)
    {
        Clipboard(true, DeleteText);
        Assert.True(UiaDesktop.ContainsProjectedDeleteCommand(Keys(keys), line: ""));
    }

    [Theory]
    [InlineData("ctrl+v")]
    [InlineData("shift+insert")]
    [InlineData("ctrl+shift+v enter")]
    public void Paste_keys_are_allowed_when_the_clipboard_is_harmless(string keys)
    {
        Clipboard(true, HarmlessText);
        Assert.False(UiaDesktop.ContainsProjectedDeleteCommand(Keys(keys), line: ""));
    }

    [Fact]
    public void A_pasted_fragment_that_completes_the_typed_line_into_a_delete_command_is_denied()
    {
        Clipboard(true, "el important.txt");
        Assert.True(UiaDesktop.ContainsProjectedDeleteCommand(Keys("ctrl+v"), line: "d"));
    }

    [Fact]
    public void A_paste_followed_by_enter_is_denied_before_anything_is_submitted()
    {
        Clipboard(true, DeleteText);
        Assert.True(UiaDesktop.ContainsProjectedDeleteCommand(Keys("ctrl+v enter"), line: ""));
    }

    [Theory]
    [InlineData("ctrl+c")]
    [InlineData("ctrl+a")]
    [InlineData("ctrl+x")]
    public void Keys_that_do_not_paste_do_not_read_the_clipboard_at_all(string keys)
    {
        PasteClipboard.Reader = () => throw new InvalidOperationException("the clipboard must not be read for " + keys);
        Assert.False(UiaDesktop.ContainsProjectedDeleteCommand(Keys(keys), line: ""));
    }

    [Fact]
    public void Right_click_in_a_terminal_is_denied_when_the_clipboard_holds_a_delete_command()
    {
        Clipboard(true, DeleteText);
        Assert.True(UiaDesktop.ContainsProjectedDeleteCommand(Act("right_click"), line: "", elementName: "Terminal"));
    }

    [Fact]
    public void Right_click_in_a_terminal_is_allowed_when_the_clipboard_is_harmless()
    {
        Clipboard(true, HarmlessText);
        Assert.False(UiaDesktop.ContainsProjectedDeleteCommand(Act("right_click"), line: "", elementName: "Terminal"));
    }

    [Theory]
    [InlineData("click")]
    [InlineData("invoke")]
    [InlineData("double_click")]
    public void A_paste_menu_item_is_denied_when_the_clipboard_holds_a_delete_command(string action)
    {
        Clipboard(true, DeleteText);
        Assert.True(UiaDesktop.ContainsProjectedDeleteCommand(Act(action), line: null, elementName: "Paste"));
    }

    [Fact]
    public void Other_menu_items_do_not_read_the_clipboard()
    {
        PasteClipboard.Reader = () => throw new InvalidOperationException("the clipboard must not be read");
        Assert.False(UiaDesktop.ContainsProjectedDeleteCommand(Act("click"), line: null, elementName: "Copy"));
        Assert.False(UiaDesktop.ContainsProjectedDeleteCommand(Act("click"), line: null, elementName: "Select all"));
    }

    [Fact]
    public void An_unreadable_clipboard_refuses_a_paste_into_a_command_window()
    {
        Clipboard(false, null);
        Assert.True(UiaDesktop.ContainsProjectedDeleteCommand(Keys("ctrl+v"), line: ""));
        Assert.True(UiaDesktop.ContainsProjectedDeleteCommand(Act("right_click"), line: "", elementName: "Terminal"));
        Assert.True(UiaDesktop.ContainsProjectedDeleteCommand(Act("click"), line: null, elementName: "Paste"));
    }

    [Fact]
    public void A_clipboard_with_no_text_cannot_be_a_command()
    {
        Clipboard(true, null);
        Assert.False(UiaDesktop.ContainsProjectedDeleteCommand(Keys("ctrl+v"), line: ""));
        Clipboard(true, string.Empty);
        Assert.False(UiaDesktop.ContainsProjectedDeleteCommand(Keys("ctrl+v"), line: ""));
    }
}

[CollectionDefinition("PasteClipboard", DisableParallelization = true)]
public sealed class PasteClipboardCollection;
