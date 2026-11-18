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
