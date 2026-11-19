using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

public sealed class TerminalSubmissionTests
{
    [Theory]
    [InlineData(null, "\n", ReasonCodes.UnsupportedAction)]
    [InlineData(null, "\r", ReasonCodes.UnsupportedAction)]
    [InlineData(null, "\r\n", ReasonCodes.UnsupportedAction)]
    [InlineData("rm -rf build", "\n", ReasonCodes.PolicyForbidden)]
    [InlineData("PS C:\\> Remove-Item build", "\r\n", ReasonCodes.PolicyForbidden)]
    [InlineData("PS C:\\> r\r\nm -rf build", "\n", ReasonCodes.PolicyForbidden)]
    [InlineData("echo safe", "\n", null)]
    [InlineData("PS C:\\> # rm example", "\n", null)]
    public void Enter_and_typed_newlines_require_complete_safe_command_text(string? line, string insertion, string? reason)
    {
        Assert.Equal(reason, UiaDesktop.SubmissionFailure(line, insertion));
    }

    // A full-screen terminal app (opencode) hides its command line from UI Automation (line == null): the window's OCR
    // text decides instead. No OCR text keeps the block; a delete command anywhere in it is refused.
    [Theory]
    [InlineData(null, "\n", ReasonCodes.UnsupportedAction)]
    [InlineData("", "\n", ReasonCodes.UnsupportedAction)]
    [InlineData("  \n \r\n", "\r", ReasonCodes.UnsupportedAction)]
    [InlineData("opencode\n\u2503 what is the weather in Seattle today?\n", "\n", null)]
    [InlineData("Jarry's local weather\n> what's the weather like?", "\r\n", null)]
    [InlineData("opencode\n\u2503 rm -rf build \u2503\n", "\n", ReasonCodes.PolicyForbidden)]
    [InlineData("\u256D\u2500\u2500\u256E\n\u2502 > Remove-Item C:\\temp -Recurse \u2502", "\n", ReasonCodes.PolicyForbidden)]
    [InlineData("PS C:\\> echo hi\nPS C:\\> del notes.txt", "\n", ReasonCodes.PolicyForbidden)]
    [InlineData("previous output\n\u276F rmdir /s old", "\r", ReasonCodes.PolicyForbidden)]
    [InlineData("opencode ask anything", "rm -rf build\n", ReasonCodes.PolicyForbidden)]
    [InlineData("PS C:\\> Remove-", "Item build\n", ReasonCodes.PolicyForbidden)]
    public void Unreadable_command_line_falls_back_to_the_window_ocr_text(string? screenText, string insertion, string? reason)
    {
        Assert.Equal(reason, UiaDesktop.SubmissionFailure(null, insertion, screenText));
    }

    [Fact]
    public void Readable_command_line_is_checked_without_the_ocr_text()
    {
        Assert.Null(UiaDesktop.SubmissionFailure("echo safe", "\n", "rm -rf build"));
        Assert.Equal(ReasonCodes.PolicyForbidden, UiaDesktop.SubmissionFailure("rm -rf build", "\n", "all clean"));
    }

    [Fact]
    public void Wrapped_command_keeps_text_before_and_after_the_caret()
    {
        const string paragraph = "previous output\r\nPS C:\\> rm -rf very long path";
        var command = TerminalCommandText.Extract(paragraph, "previous output\r\nPS C:\\> rm -rf very");
        Assert.Equal("PS C:\\> rm -rf very long path", command);
        Assert.Equal(ReasonCodes.PolicyForbidden, UiaDesktop.SubmissionFailure(command, "\n"));
    }

    [Theory]
    [InlineData("custom prompt rm x", "custom prompt ")]
    [InlineData("PS C:\\> echo\r\nPS C:\\> rm x", "PS C:\\> echo")]
    [InlineData("PS C:\\> changed", "PS C:\\> original")]
    public void Unproven_command_boundaries_are_unreadable(string paragraph, string prefix)
    {
        Assert.Null(TerminalCommandText.Extract(paragraph, prefix));
    }

    [Fact]
    public void Oversized_paragraph_is_unreadable()
    {
        Assert.Null(TerminalCommandText.Extract("PS C:\\> " + new string('x', TerminalCommandText.MaxCharacters), "PS C:\\> "));
    }
}
