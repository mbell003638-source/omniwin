using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.SystemTools;

namespace OmniWin.SystemTools.Tests;

/// <summary>F2: the owner's delete words apply to the name of a Start-menu entry. Uses the real safety gate and a fake launcher.</summary>
public sealed class AppLaunchDeleteWordTests
{
    private static (SystemToolsService Service, Harness H) Build()
    {
        var h = new Harness();
        h.Apps.Apps.Add(new InstalledApp("Uninstall OmniWin Test", "Fake.Uninstall_8wekyb3d8bbwe!App", null));
        var gate = new SafetyGate(new OmniWinConfig(), h.Kill);
        var service = new SystemToolsService(gate, h.Kill, h.Activity, h.Metrics, h.Desktop, h.Foreground, new SystemToolsOptions(),
            new SystemBackends(h.Apps, h.Launcher, h.Windows, h.Audio, h.Media, h.Info, h.Clipboard));
        return (service, h);
    }

    [Fact]
    public async Task A_start_menu_entry_named_uninstall_is_denied_and_the_launcher_is_never_called()
    {
        var (service, h) = Build();

        var result = await service.LaunchAppAsync("Uninstall OmniWin Test", null, null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Launcher.Started);
    }

    [Fact]
    public async Task An_ordinary_start_menu_app_still_launches_through_the_real_gate()
    {
        var (service, h) = Build();

        var result = await service.LaunchAppAsync("Calculator", null, null, Harness.Ct);

        Assert.NotEqual(ActStatus.Denied, result.Status);
        Assert.NotEmpty(h.Launcher.Started);
    }

    // F8: other interpreters and loaders that an owner might allow-list never receive args either.
    [Theory]
    [InlineData("C:\\Tools\\python.exe")]
    [InlineData("C:\\Tools\\node.exe")]
    [InlineData("C:\\Tools\\bash.exe")]
    [InlineData("C:\\Windows\\System32\\wsl.exe")]
    [InlineData("C:\\Windows\\System32\\rundll32.exe")]
    [InlineData("C:\\Windows\\System32\\regsvr32.exe")]
    [InlineData("C:\\Tools\\msbuild.exe")]
    [InlineData("C:\\Tools\\java.exe")]
    public async Task Args_never_reach_an_allow_listed_interpreter(string path)
    {
        var h = new Harness(new SystemToolsOptions { AppLaunchAllowPaths = [path] });

        var result = await h.Service.LaunchAppAsync(path, "some args", null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Launcher.Started);
    }
}
