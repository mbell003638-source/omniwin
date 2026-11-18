using OmniWin.Core;
using static OmniWin.Safety.Tests.DeletePolicyTests;

namespace OmniWin.Safety.Tests;

public sealed class SafetyGateTests
{
    [Fact]
    public async Task Gate_allows_ordinary_actions_without_confirmation()
    {
        var gate = new SafetyGate(new OmniWinConfig(), new StopDouble());
        var result = await gate.AuthorizeAsync(Action("click", Target(name: "Send")), TestContext.Current.CancellationToken);
        Assert.True(result.Allowed);
        Assert.False(result.UserConfirmed);
        Assert.Equal(RiskClass.Normal, result.Risk);
    }

    [Fact]
    public async Task Delete_denial_has_no_confirmation()
    {
        var gate = new SafetyGate(new OmniWinConfig(), new StopDouble());
        var result = await gate.AuthorizeAsync(Action("click", Target(name: "Delete")), TestContext.Current.CancellationToken);
        Assert.False(result.Allowed);
        Assert.False(result.UserConfirmed);
        Assert.Equal(RiskClass.Forbidden, result.Risk);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
    }

    [Fact]
    public async Task Stop_state_denies_an_otherwise_permitted_action()
    {
        var gate = new SafetyGate(new OmniWinConfig(), new StopDouble { IsTripped = true });
        var result = await gate.AuthorizeAsync(Action("click", Target(name: "Save")), TestContext.Current.CancellationToken);
        Assert.False(result.Allowed);
        Assert.Equal(ReasonCodes.KillSwitch, result.Reason);
    }

    [Fact]
    public async Task Cancelled_authorization_throws_before_approval()
    {
        var gate = new SafetyGate(new OmniWinConfig(), new StopDouble());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.AuthorizeAsync(Action("click", Target()), cancelled.Token));
    }

    [Theory]
    [InlineData("ctrl+alt+shift+k")]
    [InlineData("SHIFT+CONTROL+ALT+K")]
    [InlineData("ctrl+a ctrl+alt+shift+k")]
    public async Task Synthesized_stop_hotkey_is_denied(string keys)
    {
        var gate = new SafetyGate(new OmniWinConfig(), new StopDouble());
        var result = await gate.AuthorizeAsync(Action("keys", keys: keys), TestContext.Current.CancellationToken);
        Assert.False(result.Allowed);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
    }

    [Fact]
    public async Task Configured_stop_hotkey_is_reserved()
    {
        var gate = new SafetyGate(new OmniWinConfig { KillHotkey = "alt+f12" }, new StopDouble());
        var result = await gate.AuthorizeAsync(Action("keys", keys: "ALT+F12"), TestContext.Current.CancellationToken);
        Assert.False(result.Allowed);
    }

    [Theory]
    [InlineData("ctrl+esc", "ctrl+escape")]
    [InlineData("ctrl+f01", "ctrl+f1")]
    [InlineData("alt+ins", "alt+insert")]
    public async Task Stop_hotkey_aliases_cannot_bypass_the_gate(string configured, string keys)
    {
        var gate = new SafetyGate(new OmniWinConfig { KillHotkey = configured }, new StopDouble());
        var result = await gate.AuthorizeAsync(Action("keys", keys: keys), TestContext.Current.CancellationToken);
        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task Extra_blocked_processes_and_words_are_enforced()
    {
        var config = new OmniWinConfig { ProcessBlocklistExtra = ["private.exe"], BlockedWordsExtra = ["purge"] };
        var gate = new SafetyGate(config, new StopDouble());
        Assert.False((await gate.AuthorizeAsync(Action("click", Target(process: "PRIVATE.EXE")),
            TestContext.Current.CancellationToken)).Allowed);
        Assert.False((await gate.AuthorizeAsync(Action("click", Target(name: "Purge")),
            TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public void Framework_host_name_does_not_block_other_dotnet_applications()
    {
        var gate = new SafetyGate(new OmniWinConfig(), new StopDouble());
        Assert.Equal(RiskClass.Normal, gate.Classify(Action("click", Target(process: "dotnet.exe"))));
        Assert.Equal(RiskClass.Forbidden, gate.Classify(Action("click", Target(process: "OmniWin.Server.exe"))));
    }

    private sealed class StopDouble : IKillSwitch
    {
        public bool IsTripped { get; init; }
        public CancellationToken Token => CancellationToken.None;
    }
}
