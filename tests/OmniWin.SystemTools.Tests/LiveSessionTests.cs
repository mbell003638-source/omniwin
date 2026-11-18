using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

/// <summary>
/// Real-desktop tests (brief §6 B). They change shared desktop state (volume, clipboard), so they are tagged and skipped
/// until the owner's one live session (work order Phase 5): set OMNIWIN_LIVE_SESSION=1 with the owner present. They hold
/// <c>Local\OmniWin.UiInputTestLock</c> and put back what they change.
/// Still to write for Phase 5 because they need the WS4 fixture app: window_manage on a fixture window, app_launch of a
/// Start-menu app, screen_ocr of a fixture window, and an oN click through desktop_act (see docs/status/WS3.md).
/// </summary>
public sealed class LiveSessionTests
{
    public static bool Enabled => Environment.GetEnvironmentVariable("OMNIWIN_LIVE_SESSION") == "1";

    private const string LockName = "Local\\OmniWin.UiInputTestLock";

    private static SystemToolsService RealService() => new(
        new FakeGate([]), new FakeKill(), new FakeActivity(), new FakeMetrics(), new FakeDesktop(),
        new FakeForeground([]), new SystemToolsOptions());

    [Fact(Skip = "Live session only: sets the real volume and puts it back. Set OMNIWIN_LIVE_SESSION=1.", SkipUnless = nameof(Enabled))]
    public async Task Live_audio_set_reads_back_and_is_restored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var uiLock = await UiLockHolder.AcquireAsync(LockName, TimeSpan.FromSeconds(30));
        try
        {
            var service = RealService();
            var original = await service.GetAudioAsync(ct);
            try
            {
                var target = original.VolumePercent >= 50 ? original.VolumePercent - 10 : original.VolumePercent + 10;
                var result = await service.SetAudioAsync(target, null, ct);

                Assert.Equal(ActStatus.Confirmed, result.Status);
                Assert.Equal(target, (await service.GetAudioAsync(ct)).VolumePercent);
            }
            finally
            {
                await service.SetAudioAsync(original.VolumePercent, original.Muted, ct);
            }

            Assert.Equal(original.VolumePercent, (await service.GetAudioAsync(ct)).VolumePercent);
        }
        finally
        {
            uiLock.Dispose();
        }
    }

    [Fact(Skip = "Live session only: replaces the real clipboard text and puts it back. Set OMNIWIN_LIVE_SESSION=1.", SkipUnless = nameof(Enabled))]
    public async Task Live_clipboard_set_then_get_round_trips_and_is_restored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var uiLock = await UiLockHolder.AcquireAsync(LockName, TimeSpan.FromSeconds(30));
        try
        {
            var backend = new Win32Clipboard();
            var original = await backend.GetTextAsync(ct);
            try
            {
                var service = RealService();
                var result = await service.ClipboardSetAsync("OmniWin-test-clipboard 123", ct);

                Assert.Equal(ActStatus.Confirmed, result.Status);
                Assert.Equal("OmniWin-test-clipboard 123", await service.ClipboardGetAsync(ct));
            }
            finally
            {
                if (original is not null) await backend.SetTextAsync(original, ct);
            }
        }
        finally
        {
            uiLock.Dispose();
        }
    }
}
