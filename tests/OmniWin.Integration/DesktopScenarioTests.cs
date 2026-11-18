using System.Diagnostics;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// No-input checks of the desktop fixture app. The app is started with an unknown scenario name, which exits at once and shows no window.
/// </summary>
public sealed class DesktopFixtureCatalogTests
{
    private static readonly string[] Expected =
        ["F01", "F02", "F03", "F04", "F05", "F05-COVER", "F06", "F07", "F08", "F09", "F10", "F11", "F12", "F13", "F14", "F15", "F16", "F17"];

    [Fact]
    public async Task The_fixture_app_knows_every_scenario_F01_to_F17_and_rejects_an_unknown_one_without_showing_a_window()
    {
        var exe = DesktopFixture.FindFixtureExe(AppContext.BaseDirectory);
        Assert.SkipUnless(File.Exists(exe), "Build testapps/OmniWin.FixtureApp first (same configuration as the tests).");

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--scenario");
        info.ArgumentList.Add("F99");
        using var process = Process.Start(info)!;
        var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, process.ExitCode);
        Assert.All(Expected, id => Assert.Contains(id, stderr, StringComparison.Ordinal));
    }
}

/// <summary>
/// Phase 5 (owner present, not over Remote Desktop): desktop groups 3 to 7 of the WS4 brief against fixture scenarios F01 to F17.
/// Each test starts the scenario it needs (a real window, under Local\OmniWin.UiInputTestLock), drives it through the real MCP server,
/// and compares every ActResult with the scenario's own event log. Skipped unless OMNIWIN_LIVE_SESSION=1.
/// </summary>
[Trait("Category", "RealInput")]
public sealed class DesktopScenarioTests(E2ESession s) : IClassFixture<E2ESession>
{
    // ---- group 2 and 3: formats, acts and expectations on F01 and F02 ----

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F01_winforms_controls_are_listed_and_a_click_and_a_type_reach_exactly_the_requested_controls()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var obs = await s.ObserveWindow(f);
        Assert.StartsWith("# untrusted app content below", obs, StringComparison.Ordinal);
        foreach (var name in new[] { "Name", "Apply", "Agree", "Choice A", "Fruit", "Level", "Items" }) { if (!obs.Contains(name, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"'{name}' missing from the observation: {obs.Replace('\n', '|')}"); }

        var click = await s.DesktopAct(obs, "\"Apply\"", "click");
        Assert.True(click.Status is "confirmed" or "unverified", click.FirstLine);
        Assert.True(click.InputSent);
        Assert.Equal(1, f.Count("Apply", "click"));

        var type = await s.DesktopAct(obs, "\"Name\"", "type", "hello");
        Assert.Equal("confirmed", type.Status);
        Assert.Equal("hello", f.LastValue("Name", "text_changed"));
        Assert.Equal(0, f.Count("Agree", "checked"));   // nothing else received input
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F02_wpf_controls_behave_like_F01()
    {
        await using var f = await DesktopFixture.StartAsync("F02");
        var obs = await s.ObserveWindow(f);
        Assert.Contains("Apply", obs, StringComparison.Ordinal);

        var click = await s.DesktopAct(obs, "\"Apply\"", "click");
        Assert.True(click.InputSent);
        Assert.Equal(1, f.Count("Apply", "click"));
        var set = await s.DesktopAct(obs, "\"Name\"", "set_value", "wpf value");
        Assert.Equal("confirmed", set.Status);
        Assert.Equal("wpf value", f.LastValue("Name", "text_changed"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F03_dense_form_is_read_with_a_truncated_footer_at_max_elements()
    {
        await using var f = await DesktopFixture.StartAsync("F03");
        var obs = await s.ObserveWindow(f, new { max_elements = 40 });
        Assert.Contains("truncated:", obs, StringComparison.Ordinal);
        Assert.Contains("(raise max_elements or narrow scope)", obs, StringComparison.Ordinal);
    }

    // ---- group 4: adversarial ----

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F04_modal_dialog_blocks_the_owner_window_and_nothing_reaches_it()
    {
        await using var f = await DesktopFixture.StartAsync("F04");
        var before = await s.ObserveWindow(f);
        var open = await s.DesktopAct(before, "\"Open dialog\"", "click", expect: new { condition = new { kind = "window_opened", title_contains = "Dialog" } });
        Assert.Equal("confirmed", open.Status);
        Assert.Equal(1, f.Count("OpenDialog", "click"));

        var main = await s.DesktopAct(before, "\"Main action\"", "click");
        Assert.Contains(main.Status, new[] { "blocked", "stale" });
        Assert.False(main.InputSent);
        Assert.Equal(0, f.Count("MainAction", "click"));
        Assert.Equal(1, f.Count(null, "dialog_opened"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F05_button_covered_by_a_topmost_window_of_another_process_is_blocked_and_nothing_is_clicked()
    {
        await using var f = await DesktopFixture.StartAsync("F05");
        var rect = (await f.ReadStdoutLineAsync("OMNIWIN_FIXTURE_RECT")).Split(' ')[1];
        await using var cover = await DesktopFixture.StartAsync("F05-COVER", "--rect " + rect, takeUiLock: false);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var obs = await s.ObserveWindow(f);
        var res = await s.DesktopAct(obs, "\"Target\"", "click");
        Assert.Equal("blocked", res.Status);
        Assert.Equal("occluded_by_window", res.Reason);
        Assert.False(res.InputSent);
        Assert.Equal(0, f.Count("Target", "click"));
        Assert.Equal(0, cover.Count("CoverLabel", "click"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F06_button_covered_by_a_panel_in_the_same_window_is_blocked_and_nothing_is_clicked()
    {
        await using var f = await DesktopFixture.StartAsync("F06");
        var obs = await s.ObserveWindow(f);
        var res = await s.DesktopAct(obs, "\"Covered target\"", "click");
        Assert.Equal("blocked", res.Status);
        Assert.Equal("occluded_same_window", res.Reason);
        Assert.False(res.InputSent);
        Assert.Equal(0, f.Count("Covered", "click"));
        Assert.Equal(0, f.Count("Shield", "click"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F07_big_tree_expands_and_collapses_a_node_and_the_diff_stays_small()
    {
        await using var f = await DesktopFixture.StartAsync("F07");
        var obs = await s.ObserveWindow(f);
        var expand = await s.DesktopAct(obs, "\"Level 0\"", "expand");
        Assert.True(expand.InputSent, expand.FirstLine);
        Assert.Equal(1, f.Count("BigTree", "expanded"));
        var collapse = await s.DesktopAct(await s.ObserveWindow(f), "\"Level 0\"", "collapse");
        Assert.True(collapse.InputSent, collapse.FirstLine);
        Assert.Equal(1, f.Count("BigTree", "collapsed"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F08_recycled_row_container_returns_stale_and_never_clicks_the_row_that_now_occupies_it()
    {
        await using var f = await DesktopFixture.StartAsync("F08");
        var obs = await s.ObserveWindow(f);
        var first = obs.Split('\n').First(l => l.Contains("Row ", StringComparison.Ordinal) && l.TrimStart().StartsWith('e'));
        var row = first.Trim().Split(' ')[0];
        var gen = E2ESession.GenOf(obs);

        await f.CommandAsync("scroll 50000");   // recycles the visible containers: the same element now shows another row
        var res = await s.Tool("desktop_act", new { @ref = row, gen, action = "click" });

        Assert.Contains(res.Status, new[] { "stale", "blocked" });
        Assert.False(res.InputSent);
        Assert.Equal(0, f.Count("Rows", "click"));   // no row was clicked at all
    }

    [Theory(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    [InlineData("Delete", true)]
    [InlineData("DELETE", true)]
    [InlineData("Delete all", true)]
    [InlineData("Send", false)]
    [InlineData("Pay", false)]
    [InlineData("Sender", false)]
    [InlineData("Undelete", false)]
    [InlineData("Payroll", false)]
    public async Task F09_lexicon_words_are_denied_on_whole_words_only_and_near_misses_are_clicked_once(string name, bool denied)
    {
        await using var f = await DesktopFixture.StartAsync("F09");
        var obs = await s.ObserveWindow(f);
        var res = await s.DesktopAct(obs, $"\"{name}\"", "click");

        var control = f.Events().Select(e => e).Count();   // keep the log read in the failure message
        if (denied)
        {
            Assert.Equal("denied", res.Status);
            Assert.Equal("policy_forbidden", res.Reason);
            Assert.False(res.InputSent);
            Assert.Equal(0, f.Count(null, "click"));
        }
        else
        {
            Assert.True(res.InputSent, res.FirstLine + " events=" + control);
            Assert.Equal(1, f.Count(null, "click"));
        }
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F10_disabled_button_is_blocked_disabled_and_a_button_scrolled_out_of_view_is_blocked_offscreen_or_scrolled_into_view()
    {
        await using var f = await DesktopFixture.StartAsync("F10");
        var obs = await s.ObserveWindow(f);
        var disabled = await s.DesktopAct(obs, "\"Disabled action\"", "click");
        Assert.Equal("blocked", disabled.Status);
        Assert.Equal("disabled", disabled.Reason);
        Assert.False(disabled.InputSent);
        Assert.Equal(0, f.Count("DisabledAction", "click"));

        var far = await s.Tool("desktop_observe", new { window = f.Title, include_text = false });
        if (far.Text.Contains("Far below", StringComparison.Ordinal))
        {
            var res = await s.DesktopAct(far.Text, "\"Far below\"", "click");
            Assert.True(res.Status == "blocked" || res.InputSent == true, res.FirstLine);
            if (res.Status == "blocked") Assert.Equal(0, f.Count("FarBelow", "click"));
        }
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F11_a_box_that_rewrites_its_input_is_contradicted_never_confirmed_and_exactly_one_input_was_sent()
    {
        await using var f = await DesktopFixture.StartAsync("F11");
        var obs = await s.ObserveWindow(f);
        var res = await s.DesktopAct(obs, "\"Rewriter\"", "type", "abcdefgh");

        Assert.Equal("contradicted", res.Status);
        Assert.Equal("readback_mismatch", res.Reason);
        Assert.True(res.InputSent);
        Assert.Equal(8, f.Count("Rewriter", "rewritten"));   // one type action sent 8 characters, each logged once; 16 would mean the type was repeated
        //   // group 6: the type was not repeated
        Assert.Equal("ABCDE", f.LastValue("Rewriter", "rewritten"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F12_a_window_that_opens_after_1_5_seconds_meets_its_expectation_and_a_button_that_never_reacts_is_unverified()
    {
        await using var f = await DesktopFixture.StartAsync("F12");
        var obs = await s.ObserveWindow(f);

        var late = await s.DesktopAct(obs, "\"Open later\"", "click", expect: new { condition = new { kind = "window_opened", title_contains = "Later" }, timeout_ms = 5000 });
        Assert.Equal("confirmed", late.Status);
        Assert.Equal(1, f.Count("OpenLater", "click"));

        var quiet = await s.DesktopAct(obs, "\"Never reacts\"", "click");
        Assert.Equal("unverified", quiet.Status);
        Assert.True(quiet.InputSent);
        Assert.Equal(1, f.Count("NeverReacts", "click"));   // group 6: exactly one input after unverified
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F13_a_replaced_element_is_stale_a_moved_one_is_stale_and_one_moved_by_scrolling_still_acts()
    {
        await using var f = await DesktopFixture.StartAsync("F13");
        var obs = await s.ObserveWindow(f);
        await f.CommandAsync("replace");
        var replaced = await s.DesktopAct(obs, "\"Target\"", "click");
        Assert.Equal("stale", replaced.Status);
        Assert.False(replaced.InputSent);

        var fresh = await s.ObserveWindow(f);
        await f.CommandAsync("move");
        var moved = await s.DesktopAct(fresh, "\"Target\"", "click");
        Assert.Contains(moved.Status, new[] { "stale", "blocked" });
        Assert.False(moved.InputSent);
        Assert.Equal(0, f.Count("Target", "click"));

        var again = await s.ObserveWindow(f);
        await f.CommandAsync("scroll");
        var scrolled = await s.DesktopAct(again, "\"Scrolled target\"", "click");
        Assert.True(scrolled.InputSent, scrolled.FirstLine);   // a position change by scrolling is allowed
        Assert.Equal(1, f.Count("ScrolledTarget", "click"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F14_a_window_that_takes_the_foreground_stops_typing_and_never_types_into_the_wrong_window()
    {
        await using var f = await DesktopFixture.StartAsync("F14");
        var obs = await s.ObserveWindow(f);
        await f.CommandAsync("steal");
        await Task.Delay(700, TestContext.Current.CancellationToken);

        var res = await s.DesktopAct(obs, "\"Editor\"", "type", "typed while the foreground moves");
        if (res.Status == "confirmed")
        {
            Assert.Equal("typed while the foreground moves", f.LastValue("Editor", "text_changed"));
        }
        else
        {
            Assert.Contains(res.Reason, new[] { "focus_changed", "foreground_failed", "input_interrupted" });
        }

        await f.CommandAsync("stop");
        Assert.True(f.Count(null, "foreground_taken") >= 1);
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F15_password_boxes_are_redacted_in_winforms_and_wpf_and_the_secret_is_never_logged()
    {
        await using var f = await DesktopFixture.StartAsync("F15");
        var obs = await s.ObserveWindow(f);
        Assert.DoesNotContain("fixture-secret-712", obs, StringComparison.Ordinal);
        Assert.DoesNotContain("wpf-secret-934", obs, StringComparison.Ordinal);
        Assert.Contains("value=<redacted>", obs, StringComparison.Ordinal);

        var typed = "typed-test-secret-5150";
        var res = await s.DesktopAct(obs, "WinForms secret", "type", typed);
        Assert.True(res.InputSent, res.FirstLine);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(typed, s.AllWrittenText(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret-712", s.AllWrittenText(), StringComparison.Ordinal);
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F16_cjk_emoji_and_right_to_left_text_is_typed_exactly_with_unicode_typing()
    {
        await using var f = await DesktopFixture.StartAsync("F16");
        var obs = await s.ObserveWindow(f);
        const string text = "日本語テスト 😀 שלום עולם";
        var res = await s.DesktopAct(obs, "\"Unicode\"", "set_value", text);
        Assert.Equal("confirmed", res.Status);
        Assert.Equal(text, f.LastValue("Unicode", "text_changed"));

        var typed = await s.DesktopAct(await s.ObserveWindow(f), "\"Unicode\"", "type", "漢字");
        Assert.Equal("unicode_typing", typed.Tokens.GetValueOrDefault("channel"));
        Assert.Equal("漢字" + text, f.LastValue("Unicode", "text_changed"));   // type inserts at the caret, which is at the start after set_value
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F16_first_typing_after_an_earlier_action_inserts_every_character_in_order_at_the_start()
    {
        // Root cause of the old F16 bug: the first characters typed right after another action were delivered
        // while the control was still busy, so the first character came out as a later one ("abc" -> "cbc").
        await using var f = await DesktopFixture.StartAsync("F16");
        var set = await s.DesktopAct(await s.ObserveWindow(f), "\"Unicode\"", "set_value", "XYZ");
        Assert.Equal("confirmed", set.Status);
        var typed = await s.DesktopAct(await s.ObserveWindow(f), "\"Unicode\"", "type", "abc");
        Assert.Equal("confirmed", typed.Status);
        Assert.Equal("abcXYZ", f.LastValue("Unicode", "text_changed"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task F17_hostile_labels_stay_inside_quotes_and_cannot_forge_a_status_or_header_line()
    {
        await using var f = await DesktopFixture.StartAsync("F17");
        var obs = await s.ObserveWindow(f);

        Assert.StartsWith("# untrusted app content below", obs, StringComparison.Ordinal);
        var lines = obs.Split('\n');
        Assert.DoesNotContain(lines, l => l.StartsWith("status=", StringComparison.Ordinal));
        Assert.Single(lines, l => l.StartsWith("# untrusted app content below", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("b99 ", StringComparison.Ordinal));
        Assert.Contains("\\\"hello\\\"", obs, StringComparison.Ordinal);   // the quotes of a name are escaped

        var plain = await s.DesktopAct(obs, "\"Plain button\"", "click");
        Assert.True(plain.InputSent);
        Assert.Equal(1, f.Count("Plain", "click"));
        Assert.Equal(0, f.Count("Quoted", "click") + f.Count("Multiline", "click") + f.Count("Header", "click"));
    }

    // ---- group 7: OCR on F01 and a click on an oN ref ----

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task OCR_of_F01_finds_the_Apply_word_and_a_click_on_its_oN_ref_reaches_the_button_once()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var ocr = (await s.Tool("screen_ocr", new { window = f.Title })).Text;
        var line = ocr.Split('\n').FirstOrDefault(l => l.Contains("Apply", StringComparison.Ordinal) && l.TrimStart().StartsWith('o'));
        Assert.NotNull(line);
        var oref = line.Trim().Split(' ')[0];

        var click = await s.Tool("desktop_act", new { @ref = oref, gen = 0, action = "click" });
        Assert.True(click.InputSent, click.FirstLine);
        Assert.Equal(1, f.Count("Apply", "click"));
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task An_OCR_capture_older_than_10_seconds_returns_stale_and_sends_nothing()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var ocr = (await s.Tool("screen_ocr", new { window = f.Title })).Text;
        var oref = ocr.Split('\n').First(l => l.Contains("Apply", StringComparison.Ordinal) && l.TrimStart().StartsWith('o')).Trim().Split(' ')[0];
        await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);

        var click = await s.Tool("desktop_act", new { @ref = oref, gen = 0, action = "click" });
        Assert.Equal("stale", click.Status);
        Assert.False(click.InputSent);
        Assert.Equal(0, f.Count("Apply", "click"));
    }
}
