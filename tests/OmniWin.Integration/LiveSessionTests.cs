using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// Phase 5 (owner present, not over Remote Desktop). Everything here sends real input, changes the system or shows a window,
/// so it is skipped unless OMNIWIN_LIVE_SESSION=1. Run it with the command in docs/status/WS4.md. Each test restores what it changes.
/// Desktop adversarial cases F05 (covered by another process), F09 (desktop delete words), F13 (recycled row), F14 (foreground
/// thief) need the desktop fixture scenarios, which are not built yet (docs/status/WS4.md, finding 6).
/// </summary>
[Trait("Category", "RealInput")]
public sealed class LiveSessionTests(E2ESession s) : IClassFixture<E2ESession>
{
    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Audio_set_reads_back_and_is_restored()
    {
        var before = (await s.Tool("audio", new { })).Text;
        Assert.Matches(@"volume=\d+", before);
        var volume = int.Parse(System.Text.RegularExpressions.Regex.Match(before, @"volume=(\d+)").Groups[1].Value);
        var target = volume >= 50 ? volume - 5 : volume + 5;
        try
        {
            var set = await s.Tool("audio", new { volume = target });
            Assert.True(set.Status is "confirmed" or "unverified", set.FirstLine);
            Assert.Contains($"volume={target}", (await s.Tool("audio", new { })).Text, StringComparison.Ordinal);
        }
        finally
        {
            await s.Tool("audio", new { volume });
        }
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Clipboard_set_then_get_round_trips_and_the_original_text_is_put_back()
    {
        // The harness saves the owner's clipboard itself (Win32, exact text) and puts it back, also when it was empty. A clipboard
        // holding something other than text could not be put back, so then the test touches nothing.
        if (!ClipboardState.TryRead(out var original, out var why)) Assert.Skip("Not run, nothing touched: " + why + ".");
        if (original is { Length: > 1_000_000 }) Assert.Skip("Not run, nothing touched: the clipboard text is longer than clipboard_set can put back.");
        var marker = "OmniWin-test-" + Guid.NewGuid().ToString("N");
        try
        {
            var set = await s.Tool("clipboard_set", new { text = marker });
            Assert.True(set.InputSent == false || set.Status is "confirmed" or "unverified", set.FirstLine);
            var got = await s.Tool("clipboard_get", new { });
            Assert.Contains(marker, got.Text, StringComparison.Ordinal);
            Assert.Contains("risk=risky", got.FirstLine, StringComparison.Ordinal);   // clipboard_get is labelled Risky and never blocked
        }
        finally
        {
            if (original is null) Assert.True(ClipboardState.Empty(), "the clipboard was empty before the test and could not be emptied again");
            else Assert.Equal("confirmed", (await s.Tool("clipboard_set", new { text = original })).Status);
        }
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Windows_list_shows_real_windows_and_a_window_manage_minimize_restore_round_trip_works()
    {
        // Only a window this test started is minimized: the first window of the list could be the taskbar or the owner's own app.
        await using var fixture = await DesktopFixture.StartAsync("F01");
        var list = (await s.Tool("windows_list", new { })).Text;
        var first = list.Split('\n').FirstOrDefault(l => l.StartsWith('w') && l.Contains(fixture.Title, StringComparison.Ordinal));
        Assert.NotNull(first);
        var id = first.Split(' ')[0];

        // The window's own line in a fresh windows_list says whether it is minimized: that is the check, not input_sent.
        async Task<bool?> MinimizedNow()
        {
            var line = (await s.Tool("windows_list", new { })).Text.Split('\n').FirstOrDefault(l => l.StartsWith(id + " ", StringComparison.Ordinal));
            return line is null ? null : line.Contains("minimized", StringComparison.Ordinal);
        }

        async Task<bool> Becomes(bool minimized)
        {
            for (var i = 0; i < 20; i++)
            {
                if (await MinimizedNow() == minimized) return true;
                await Task.Delay(150);
            }

            return false;
        }

        Assert.False(await MinimizedNow() ?? true, "the fixture window should start not minimized");
        try
        {
            Assert.True((await s.Tool("window_manage", new { window = id, action = "minimize" })).InputSent == true);
            Assert.True(await Becomes(minimized: true), "the window was not minimized");
        }
        finally
        {
            await s.Tool("window_manage", new { window = id, action = "restore" });
        }

        Assert.True(await Becomes(minimized: false), "the window was not restored");
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public void Screen_ocr_of_a_fixture_window_an_oN_click_and_the_ten_second_stale_rule()
    {
        // Covered for real by DesktopScenarioTests (F01 exists now): OCR_of_F01_finds_the_Apply_word_and_a_click_on_its_oN_ref_reaches_
        // the_button_once and An_OCR_capture_older_than_10_seconds_returns_stale_and_sends_nothing. Kept as a pointer, never a red result.
        Assert.Skip("Covered by the two OCR tests in DesktopScenarioTests (F01 fixture).");
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public void Stop_key_cancels_in_flight_work_and_later_acts_return_denied_kill_switch()
    {
        // Only the user's real keyboard can press Ctrl+Alt+Shift+K (injected keys are ignored by design); this is on the manual checklist.
        Assert.Skip("Manual check: press Ctrl+Alt+Shift+K during a running act (see the manual checklist in docs/status/WS4.md).");
    }
}
