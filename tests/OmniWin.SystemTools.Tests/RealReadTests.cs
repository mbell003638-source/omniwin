using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

/// <summary>
/// Read-only calls against the real machine. They change nothing, show no window and send no input. Setting audio,
/// moving windows, launching apps and the clipboard are NOT exercised for real here: those are listed as Phase 5
/// real-desktop tests (see docs/status/WS3.md) because they change shared desktop state.
/// </summary>
public sealed class RealReadTests
{
    [Theory]
    [InlineData("network", "connected=")]
    [InlineData("battery", "batter")]
    [InlineData("displays", "display")]
    [InlineData("time", "utc_offset=")]
    public async Task Real_system_info_kinds_return_text(string kind, string expected)
    {
        var text = await new WindowsSystemInfo().GetAsync(kind, TestContext.Current.CancellationToken);

        Assert.Contains(expected, text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public async Task Real_network_info_contains_no_addresses()
    {
        var text = await new WindowsSystemInfo().GetAsync("network", TestContext.Current.CancellationToken);

        Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", text);
        Assert.DoesNotMatch("(?i)[0-9a-f]{2}(-[0-9a-f]{2}){5}", text);
    }

    [Fact]
    public async Task Real_start_menu_lists_apps_with_names_and_launch_ids()
    {
        var apps = await new ShellAppCatalog().ListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(apps);
        Assert.All(apps, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.False(string.IsNullOrWhiteSpace(a.LaunchId));
        });
    }

    [Fact]
    public void Real_audio_read_gives_a_valid_state_or_reports_no_device()
    {
        try
        {
            var state = new CoreAudioBackend().Get();
            Assert.InRange(state.VolumePercent, 0, 100);
            Assert.False(string.IsNullOrWhiteSpace(state.DeviceName));
        }
        catch (NoAudioDeviceException)
        {
            // A PC without an output device is a valid answer.
        }
    }

    [Fact]
    public async Task Real_media_state_read_does_not_throw()
    {
        var state = await new MediaSessionBackend().GetStateAsync(TestContext.Current.CancellationToken);

        Assert.True(state is null || Enum.IsDefined(state.Value));
    }

    [Fact]
    public void Real_window_read_of_a_handle_that_does_not_exist_says_so()
    {
        var state = new Win32WindowOps().Read(0x7FFFFFFE);

        Assert.False(state.Exists);
    }
}
