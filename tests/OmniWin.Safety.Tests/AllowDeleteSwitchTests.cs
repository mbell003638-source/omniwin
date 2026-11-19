using OmniWin.Core;
using static OmniWin.Safety.Tests.DeletePolicyTests;

namespace OmniWin.Safety.Tests;

/// <summary><c>safety.allow_delete</c> (owner decision 2026-11-19): off by default; on, deleting is allowed and labelled risky.</summary>
public sealed class AllowDeleteSwitchTests : IDisposable
{
    private static readonly OmniWinConfig On = new() { Safety = new SafetyConfig { AllowDelete = true } };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "omniwin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    public static TheoryData<ActionDescriptor> DeleteActions() =>
    [
        Action("click", Target(name: "Delete")),
        Action("invoke", Target(name: "Move to the recycle bin")),
        Action("type", process: "WindowsTerminal.exe", value: "rm -rf build"),
        Action("type", process: "pwsh.exe", value: "<12 chars>") with { ContainsDeleteCommand = true },
        Action("keys", Target(process: "notepad.exe", name: "Body", role: "edit"), keys: "shift+delete"),
        Action("drag", Target(process: "explorer.exe", name: "report.docx", role: "listitem")) with
            { DropTarget = Target(process: "explorer.exe", name: "Recycle Bin", role: "listitem") },
    ];

    [Theory]
    [MemberData(nameof(DeleteActions))]
    public async Task Off_by_default_deleting_is_denied(ActionDescriptor action)
    {
        var gate = new SafetyGate(new OmniWinConfig(), new Stop());
        var result = await gate.AuthorizeAsync(action, TestContext.Current.CancellationToken);
        Assert.False(result.Allowed);
        Assert.Equal(RiskClass.Forbidden, result.Risk);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
        Assert.Equal(RiskClass.Forbidden, gate.Classify(action));
        Assert.Equal("no_delete", gate.State);
    }

    [Theory]
    [MemberData(nameof(DeleteActions))]
    public async Task On_deleting_is_allowed_and_labelled_risky(ActionDescriptor action)
    {
        var gate = new SafetyGate(On, new Stop());
        var result = await gate.AuthorizeAsync(action, TestContext.Current.CancellationToken);
        Assert.True(result.Allowed);
        Assert.Equal(RiskClass.Risky, result.Risk);
        Assert.Null(result.Reason);
        Assert.NotNull(result.Detail);
        Assert.Equal(RiskClass.Risky, gate.Classify(action));
        Assert.Equal("off", gate.State);
    }

    [Fact]
    public async Task On_ordinary_actions_stay_normal()
    {
        var gate = new SafetyGate(On, new Stop());
        var result = await gate.AuthorizeAsync(Action("click", Target(name: "Send")), TestContext.Current.CancellationToken);
        Assert.True(result.Allowed);
        Assert.Equal(RiskClass.Normal, result.Risk);
    }

    [Fact]
    public async Task On_blocked_processes_the_stop_key_and_the_kill_switch_still_refuse()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = On with { ProcessBlocklistExtra = ["private.exe"] };
        var gate = new SafetyGate(config, new Stop());
        Assert.False((await gate.AuthorizeAsync(Action("click", Target(process: "OmniWin.Server.exe", name: "Delete")), ct)).Allowed);
        Assert.False((await gate.AuthorizeAsync(Action("click", Target(process: "PRIVATE.EXE")), ct)).Allowed);
        Assert.Equal(RiskClass.Forbidden, gate.Classify(Action("click", Target(process: "OmniWin.Server.exe"))));
        var stopKey = await gate.AuthorizeAsync(Action("keys", keys: "ctrl+alt+shift+k"), ct);
        Assert.False(stopKey.Allowed);
        Assert.Equal(ReasonCodes.PolicyForbidden, stopKey.Reason);

        var stopped = new SafetyGate(On, new Stop { IsTripped = true });
        var tripped = await stopped.AuthorizeAsync(Action("click", Target(name: "Delete")), ct);
        Assert.False(tripped.Allowed);
        Assert.Equal(ReasonCodes.KillSwitch, tripped.Reason);
    }

    [Fact]
    public void Config_key_is_off_by_default_and_validated_strictly()
    {
        Assert.False(new OmniWinConfig().Safety.AllowDelete);
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "config.json");

        File.WriteAllText(file, """{ "safety": { "allow_delete": true } }""");
        Assert.Empty(ConfigStore.Check(file));
        Assert.True(ConfigStore.LoadOrCreate(OmniWinPaths.FromOverride(_dir)).Safety.AllowDelete);

        File.WriteAllText(file, """{ "safety": { "allow_delete": false } }""");
        Assert.Empty(ConfigStore.Check(file));

        File.WriteAllText(file, """{ "safety": { "allow_deletes": true } }""");
        Assert.NotEmpty(ConfigStore.Check(file));

        File.WriteAllText(file, """{ "safety": { "allow_delete": "yes" } }""");
        Assert.NotEmpty(ConfigStore.Check(file));
    }

    private sealed class Stop : IKillSwitch
    {
        public bool IsTripped { get; init; }
        public CancellationToken Token => CancellationToken.None;
    }
}