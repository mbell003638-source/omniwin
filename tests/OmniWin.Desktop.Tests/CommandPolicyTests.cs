using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

public sealed class CommandPolicyTests
{
    [Theory]
    [InlineData("d", "el x", true)]
    [InlineData("r", "m file", true)]
    [InlineData("echo ", "hello", false)]
    [InlineData("echo hello", " world", false)]
    public void Split_typing_checks_the_existing_command(string line, string insertion, bool denied)
    {
        Assert.Equal(denied, UiaDesktop.ContainsProjectedDeleteCommand(
            new DesktopCommand("desktop_act", "type", Value: insertion), line));
    }

    [Theory]
    [InlineData("", "d e l space x enter", true)]
    [InlineData("sudo rm x", "enter", true)]
    [InlineData("echo safe", "enter", false)]
    [InlineData("del x", "ctrl+c", false)]
    [InlineData("del x", "backspace", false)]
    [InlineData("PS C:\\> d\r\n", "e l space x enter", true)]
    [InlineData("", "e c h o space h i enter", false)]
    [InlineData("", "e c h o enter r m space x enter", true)]
    public void Key_sequences_check_commands_before_submission(string line, string keys, bool denied)
    {
        Assert.Equal(denied, UiaDesktop.ContainsProjectedDeleteCommand(
            new DesktopCommand("desktop_keys", "keys", Keys: KeySequence.Parse(keys)), line));
    }
}
