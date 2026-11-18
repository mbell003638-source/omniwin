using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// Contract 2.5, live (owner present over Remote Desktop, hands off): hover, drag, labelled views, the quick router and one acting call
/// through each bridged engine. Every effect is checked against something independent of OmniWin's own answer: the fixture's own event
/// log, the real cursor position and button state (Win32), or the system volume read back. Skipped unless OMNIWIN_LIVE_SESSION=1 and a
/// connected desktop. Each test holds the shared UI lock while its fixture window exists. Volume and mute are put back.
/// </summary>
[Trait("Category", "RealInput")]
public sealed class Contract25LiveTests(E2ESession s) : IClassFixture<E2ESession>
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string title);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    private const int VkLButton = 0x01;

    private static bool LeftButtonDown() => (GetAsyncKeyState(VkLButton) & 0x8000) != 0;

    private static (int X, int Y) Cursor()
    {
        Assert.True(GetCursorPos(out var p));
        return (p.X, p.Y);
    }

    private static Rect WindowRect(string title)
    {
        var hwnd = FindWindowW(null, title);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        Assert.True(GetWindowRect(hwnd, out var rect));
        return rect;
    }

    // ------------------------------------------------------------------ hover

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Hover_moves_the_real_pointer_onto_the_control_presses_nothing_and_clicks_nothing()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var obs = await s.ObserveWindow(f);

        var hover = await s.DesktopAct(obs, "\"Apply\"", "hover");

        Assert.True(hover.Status is "confirmed" or "unverified", hover.FirstLine);
        Assert.True(hover.InputSent);
        Assert.Equal("physical_hover", hover.Tokens["channel"]);
        Assert.Contains("retry=", hover.FirstLine, StringComparison.Ordinal);
        var rect = WindowRect(f.Title);
        var (x, y) = Cursor();
        Assert.InRange(x, rect.Left, rect.Right);
        Assert.InRange(y, rect.Top, rect.Bottom);
        Assert.False(LeftButtonDown(), "the left button is down after a hover");
        Assert.Equal(0, f.InputEventCount());          // nothing the app can log as input arrived
        Assert.Equal(0, f.Count("Apply", "click"));
    }

    // ------------------------------------------------------------------ drag

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Drag_between_list_items_presses_moves_and_releases_and_the_button_is_up_afterwards()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var obs = await s.ObserveWindow(f);

        var drag = await s.DesktopAct(obs, "\"Item 1\"", "drag", value: DesktopSteps.DesktopRef(obs, "\"Item 4\""));

        Assert.True(drag.Status is "confirmed" or "unverified", drag.FirstLine);
        Assert.True(drag.InputSent);
        Assert.Equal("physical_drag", drag.Tokens["channel"]);
        // F01 starts with Item 1 already selected, so pressing it logs nothing; the list's own log must end on Item 4 (the drop target).
        var selected = f.Events().Where(e => e.GetProperty("event").GetString() == "selected" &&
            e.TryGetProperty("control", out var c) && c.GetString() == "Items").Select(e => e.GetProperty("value").GetString()).ToList();
        Assert.True(selected.Count >= 1, "selection events: " + string.Join(", ", selected));
        Assert.Equal("Item 4", selected[^1]);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(LeftButtonDown(), "the mouse button was left held down after the drag");
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Drag_with_a_drop_target_that_is_not_an_element_ref_sends_nothing()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var obs = await s.ObserveWindow(f);

        var drag = await s.DesktopAct(obs, "\"Item 1\"", "drag", value: "w2");

        Assert.Equal("blocked", drag.Status);
        Assert.Equal("unsupported_action", drag.Reason);
        Assert.False(drag.InputSent);
        Assert.Equal(0, f.InputEventCount());
        Assert.False(LeftButtonDown());
    }

    // ------------------------------------------------------------------ labelled view

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task A_labelled_view_names_how_many_refs_it_drew_and_the_generation_they_belong_to()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var obs = await s.ObserveWindow(f);
        var gen = E2ESession.GenOf(obs);

        var view = await s.Tool("screen_view", new { window = f.Title, labels = true });

        Assert.Contains("labels=", view.Text, StringComparison.Ordinal);
        var count = int.Parse(Regex.Match(view.Text, @"labels=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(count >= 10, view.Text);
        Assert.Contains($"refs_gen={gen}", view.Text, StringComparison.Ordinal);
        Assert.Equal(0, f.InputEventCount());   // a view sends no input
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task A_labelled_view_of_a_window_that_was_never_observed_or_of_the_whole_screen_is_refused_with_nothing_sent()
    {
        await using var f = await DesktopFixture.StartAsync("F01");

        var unobserved = await s.Tool("screen_view", new { window = f.Title, labels = true });
        var screen = await s.Tool("screen_view", new { window = "screen", labels = true });

        Assert.Contains("input_sent=false", unobserved.FirstLine, StringComparison.Ordinal);
        Assert.Contains("reason=not_found", unobserved.FirstLine, StringComparison.Ordinal);
        Assert.Contains("input_sent=false", screen.FirstLine, StringComparison.Ordinal);
        Assert.Contains("reason=unsupported_action", screen.FirstLine, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the quick router

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Omniwin_do_sets_the_real_volume_and_the_original_volume_is_put_back()
    {
        var before = (await s.Tool("audio", new { })).Text;
        var volume = int.Parse(Regex.Match(before, @"volume=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var muted = Regex.Match(before, @"muted=(true|false)").Groups[1].Value == "true";
        var target = volume >= 50 ? volume - 7 : volume + 7;
        try
        {
            var done = await s.Tool("omniwin_do", new { text = $"please set the volume to {target}" });
            Assert.True(done.Status is "confirmed" or "unverified", done.FirstLine);
            Assert.Contains("routed rule=volume.set", done.Text, StringComparison.Ordinal);
            Assert.Contains($"volume={target}", (await s.Tool("audio", new { })).Text, StringComparison.Ordinal);

            var up = await s.Tool("omniwin_do", new { text = "volume up" });
            Assert.True(up.Status is "confirmed" or "unverified", up.FirstLine);
            Assert.Contains($"volume={Math.Min(100, target + 10)}", (await s.Tool("audio", new { })).Text, StringComparison.Ordinal);

            var mute = await s.Tool("omniwin_do", new { text = "mute" });
            Assert.True(mute.Status is "confirmed" or "unverified", mute.FirstLine);
            Assert.Contains("muted=true", (await s.Tool("audio", new { })).Text, StringComparison.Ordinal);
            var unmute = await s.Tool("omniwin_do", new { text = "unmute" });
            Assert.True(unmute.Status is "confirmed" or "unverified", unmute.FirstLine);
            Assert.Contains("muted=false", (await s.Tool("audio", new { })).Text, StringComparison.Ordinal);
        }
        finally
        {
            await s.Tool("audio", new { volume, mute = muted });
        }
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Omniwin_do_minimizes_restores_and_switches_to_a_window_by_its_title()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var name = f.Title.ToLowerInvariant();   // "omniwin fixture f01 abc123": a unique title fragment

        var minimize = await s.Tool("omniwin_do", new { text = "minimize " + name });
        Assert.True(minimize.Status is "confirmed" or "unverified", minimize.Text);
        Assert.Contains("routed rule=window.minimize", minimize.Text, StringComparison.Ordinal);
        Assert.Contains("minimized", (await s.Tool("windows_list", new { })).Text.Split('\n').First(l => l.Contains(f.Title, StringComparison.Ordinal)), StringComparison.Ordinal);

        var restore = await s.Tool("omniwin_do", new { text = "restore " + name });
        Assert.True(restore.Status is "confirmed" or "unverified", restore.Text);
        Assert.DoesNotContain("minimized", (await s.Tool("windows_list", new { })).Text.Split('\n').First(l => l.Contains(f.Title, StringComparison.Ordinal)), StringComparison.Ordinal);

        var focus = await s.Tool("omniwin_do", new { text = "switch to " + name });
        Assert.True(focus.Status is "confirmed" or "unverified", focus.Text);
        Assert.Contains("foreground", (await s.Tool("windows_list", new { })).Text.Split('\n').First(l => l.Contains(f.Title, StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(0, f.InputEventCount());
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Omniwin_do_leaves_an_open_ended_request_and_a_risky_one_alone_while_a_window_is_open()
    {
        await using var f = await DesktopFixture.StartAsync("F01");

        var joke = await s.Tool("omniwin_do", new { text = "find me a good thriller and play it" });
        var risky = await s.Tool("omniwin_do", new { text = "delete everything in this window" });

        Assert.StartsWith("needs_brain reason=no_rule input_sent=false", joke.Text, StringComparison.Ordinal);
        Assert.StartsWith("needs_brain reason=risky_words input_sent=false", risky.Text, StringComparison.Ordinal);
        Assert.Equal(0, f.InputEventCount());
    }

    // ------------------------------------------------------------------ one acting call through each engine

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task An_agent_desktop_click_through_the_bridge_reaches_exactly_the_requested_button_and_is_reported_unverified()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var snapshot = await s.Tool("engine_call", new { engine = "agent-desktop", tool = "desktop_snapshot", args = new { app = "OmniWin.FixtureApp" } });
        Assert.Contains("input_sent=false", snapshot.FirstLine, StringComparison.Ordinal);
        // the node named Apply carries its ref_id; find it in the JSON text
        var match = Regex.Match(snapshot.Text, "\"name\":\"Apply\"[^{}]*?\"ref_id\":\"(@[^\"]+)\"|\"ref_id\":\"(@[^\"]+)\"[^{}]*?\"role\":\"[a-z_]*\"[^{}]*?\"name\":\"Apply\"|\"name\":\"Apply\",.*?\"ref_id\":\"(@[^\"]+)\"");
        Assert.True(match.Success, "no ref for Apply in the snapshot: " + snapshot.Text[..Math.Min(1500, snapshot.Text.Length)]);
        var reference = match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;

        var click = await s.Tool("engine_call", new { engine = "agent-desktop", tool = "desktop_click", args = new { ref_id = reference } });

        Assert.Equal("unverified", click.Status);          // never confirmed: OmniWin cannot check another engine's work
        Assert.True(click.InputSent);
        Assert.Contains("retry=check_state", click.FirstLine, StringComparison.Ordinal);
        Assert.Contains("engine=agent-desktop", click.Text, StringComparison.Ordinal);
        Assert.Equal(1, f.Count("Apply", "click"));         // the app's own log proves it arrived, once
        Assert.Equal(1, f.InputEventCount());
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task A_Windows_MCP_click_through_the_bridge_reaches_exactly_the_requested_button_and_is_reported_unverified()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var snapshot = await s.Tool("engine_call", new { engine = "windows-mcp", tool = "Snapshot", args = new { use_ui_tree = true } });
        Assert.Contains("input_sent=false", snapshot.FirstLine, StringComparison.Ordinal);
        // the UI tree lists "(x,y) button "Apply"" with screen coordinates
        var match = Regex.Match(snapshot.Text, @"\((\d+),(\d+)\) button \\?""Apply\\?""");
        Assert.True(match.Success, "no Apply line in the snapshot: " + snapshot.Text[..Math.Min(1500, snapshot.Text.Length)]);
        var x = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var y = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        var click = await s.Tool("engine_call", new { engine = "windows-mcp", tool = "Click", args = new { loc = new[] { x, y } } });

        Assert.Equal("unverified", click.Status);
        Assert.True(click.InputSent);
        Assert.Contains("retry=check_state", click.FirstLine, StringComparison.Ordinal);
        Assert.Equal(1, f.Count("Apply", "click"));
        Assert.Equal(1, f.InputEventCount());
        Assert.False(LeftButtonDown());
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task While_an_engine_call_is_not_running_the_input_lane_is_free_for_OmniWins_own_acts()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var obs = await s.ObserveWindow(f);

        var click = await s.DesktopAct(obs, "\"Apply\"", "click");

        Assert.True(click.Status is "confirmed" or "unverified", click.FirstLine);
        Assert.Equal(1, f.Count("Apply", "click"));
    }
}
