using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

/// <summary>Timeouts, cancellation and the stop key: input_sent must stay accurate on every path.</summary>
public sealed class PipelineTests
{
    [Fact]
    public async Task A_hung_launch_returns_engine_timeout_with_input_sent_true()
    {
        var h = new Harness(new SystemToolsOptions { ShellTimeoutMs = 150 });
        h.Launcher.Hook = async ct => await Task.Delay(Timeout.Infinite, ct);

        var result = await h.Service.LaunchAppAsync("Calculator", null, null, Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.EngineTimeout, result.Reason);
        Assert.True(result.InputSent); // the launch had already been started
    }

    [Fact]
    public async Task A_hung_gate_returns_engine_timeout_with_input_sent_false()
    {
        var h = new Harness(new SystemToolsOptions { OperationTimeoutMs = 150 });
        h.AddWindow("w1", 1001, "fixture.exe", "Fixture window");
        h.Gate.Decide = _ => throw new InvalidOperationException("not used");
        var slow = new SlowGate(h.Log);
        var service = new SystemToolsService(slow, h.Kill, h.Activity, h.Metrics, h.Desktop, h.Foreground,
            new SystemToolsOptions { OperationTimeoutMs = 150 },
            new SystemBackends(h.Apps, h.Launcher, h.Windows, h.Audio, h.Media, h.Info, h.Clipboard));

        var result = await service.WindowManageAsync(new WindowManageRequest("w1", "minimize"), Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.EngineTimeout, result.Reason);
        Assert.False(result.InputSent);
        Assert.False(h.Windows.Read(1001).Minimized);
    }

    [Fact]
    public async Task A_hung_audio_backend_returns_engine_timeout_with_input_sent_true_after_the_set_began()
    {
        var h = new Harness(new SystemToolsOptions { OperationTimeoutMs = 150 });
        h.Audio.Hook = _ =>
        {
            Thread.Sleep(2000);
            return Task.CompletedTask;
        };

        var result = await h.Service.SetAudioAsync(10, null, Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.EngineTimeout, result.Reason);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task The_stop_key_tripped_mid_call_before_input_returns_denied_with_nothing_sent()
    {
        var h = new Harness();
        h.AddWindow("w1", 1001, "fixture.exe", "Fixture window");
        var gate = new TripGate(h.Kill);
        var service = new SystemToolsService(gate, h.Kill, h.Activity, h.Metrics, h.Desktop, h.Foreground, new SystemToolsOptions(),
            new SystemBackends(h.Apps, h.Launcher, h.Windows, h.Audio, h.Media, h.Info, h.Clipboard));

        var result = await service.WindowManageAsync(new WindowManageRequest("w1", "minimize"), Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.KillSwitch, result.Reason);
        Assert.False(result.InputSent);
        Assert.False(h.Windows.Read(1001).Minimized);
    }

    [Fact]
    public async Task The_stop_key_tripped_after_input_began_returns_input_interrupted_with_input_sent_true()
    {
        var h = new Harness();
        h.Audio.Hook = async _ =>
        {
            h.Kill.Trip();
            await Task.Delay(Timeout.Infinite, h.Kill.Token);
        };

        var result = await h.Service.SetAudioAsync(10, null, Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.Equal(ReasonCodes.InputInterrupted, result.Reason);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task A_caller_cancel_before_input_reports_nothing_sent()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await h.Service.MediaAsync("stop", cts.Token);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.False(result.InputSent);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task A_backend_exception_never_escapes_and_is_reported_with_accurate_input_sent()
    {
        var h = new Harness();
        h.Media.State = MediaPlayback.Playing;
        var service = new SystemToolsService(h.Gate, h.Kill, h.Activity, h.Metrics, h.Desktop, h.Foreground, new SystemToolsOptions(),
            new SystemBackends(h.Apps, h.Launcher, h.Windows, h.Audio, new ThrowingMedia(), h.Info, h.Clipboard));

        var result = await service.MediaAsync("stop", Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.False(result.InputSent);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task Every_op_is_recorded_in_metrics()
    {
        var h = new Harness();

        await h.Service.GetAudioAsync(Harness.Ct);
        await h.Service.SystemInfoAsync("time", Harness.Ct);
        await h.Service.ListAppsAsync(null, Harness.Ct);
        await h.Service.ClipboardSetAsync("x", Harness.Ct);
        await h.Service.ClipboardGetAsync(Harness.Ct);

        var ops = h.Metrics.Records.Select(r => r.Op).ToHashSet();
        Assert.Superset(new HashSet<string> { "audio", "system_info", "apps_list", "clipboard_set", "clipboard_get" }, ops);
    }

    [Fact]
    public void The_component_reports_ready()
    {
        var h = new Harness();

        Assert.Equal("system_tools", h.Service.Component);
        Assert.Equal("ready", h.Service.State);
    }

    private sealed class SlowGate(List<string> log) : ISafetyGate
    {
        public RiskClass Classify(ActionDescriptor a) => RiskClass.Normal;

        public async Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct)
        {
            log.Add("gate");
            await Task.Delay(Timeout.Infinite, ct);
            return new GateResult(true, RiskClass.Normal, null, false);
        }
    }

    private sealed class TripGate(FakeKill kill) : ISafetyGate
    {
        public RiskClass Classify(ActionDescriptor a) => RiskClass.Normal;

        public Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct)
        {
            kill.Trip();
            return Task.FromResult(new GateResult(true, RiskClass.Normal, null, false));
        }
    }

    private sealed class ThrowingMedia : IMediaBackend
    {
        public Task<MediaPlayback?> GetStateAsync(CancellationToken ct) => throw new InvalidOperationException("media service is down");

        public Task<bool> SendAsync(string action, CancellationToken ct) => throw new InvalidOperationException("media service is down");
    }
}
