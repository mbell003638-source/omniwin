using System.Text.Json;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// Group 3 (WS4 brief §4): browser acts and expectations over stdio, headless. Ground truth is the page's own event
/// trace (posted to the fixture web server), never OmniWin's output. Wrong-target count must be zero.
/// </summary>
public sealed class BrowserActTests(E2ESession s) : IClassFixture<E2ESession>
{
    private static readonly string[] InputKinds = ["click", "dblclick", "input", "change", "keydown", "submit"];

    private static object Expect(object condition, int timeoutMs = 3000) => new { condition, timeout_ms = timeoutMs };

    [Fact]
    public async Task Set_value_is_confirmed_and_only_the_requested_control_saw_input()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Act(obs, "Text Input", "set_value", "typed-by-test");

        Assert.Equal("confirmed", r.Status);
        Assert.True(r.InputSent);
        Assert.Equal("set_value", r.Tokens["channel"]);
        var events = await s.Web.SettledAsync("controls.html");
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal("input-text", e.Id));   // wrong-target gate: nothing else saw input
        Assert.Single(events, e => e.Type == "change");
    }

    [Fact]
    public async Task Type_is_confirmed_with_real_trusted_input_on_the_requested_control_only()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Act(obs, "Text Input", "type", "X");

        Assert.Equal("confirmed", r.Status);
        Assert.Equal("cdp_insert_text", r.Tokens["channel"]);
        var events = (await s.Web.SettledAsync("controls.html")).Where(e => e.Type == "input").ToList();
        Assert.Single(events);
        Assert.Equal("input-text", events[0].Id);
        Assert.True(events[0].Trusted);
        Assert.Contains("X", events[0].Value, StringComparison.Ordinal);   // type inserts at the caret, so the value is "Xinitial text"
    }

    [Fact]
    public async Task Check_and_select_are_confirmed_and_touch_only_their_own_control()
    {
        var obs = await s.Open("controls.html");
        var check = await s.Act(obs, "I agree", "check");
        Assert.Equal("confirmed", check.Status);
        obs = await s.Observe();
        var select = await s.Act(obs, "Choice", "select", "beta");
        Assert.Equal("confirmed", select.Status);

        var events = await s.Web.SettledAsync("controls.html");
        Assert.All(events, e => Assert.Contains(e.Id, new[] { "check-agree", "select-choice" }));
        Assert.Contains(events, e => e.Id == "select-choice" && e.Type == "change" && e.Value == "beta");
    }

    [Fact]
    public async Task Click_is_unverified_without_an_expectation_and_the_page_saw_exactly_one_click()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Act(obs, "Click Me", "click");

        Assert.Equal("unverified", r.Status);
        Assert.True(r.InputSent);
        Assert.Equal("cdp_mouse", r.Tokens["channel"]);
        var clicks = (await s.Web.SettledAsync("controls.html")).Where(e => e.Type == "click").ToList();
        Assert.Single(clicks);
        Assert.Equal("btn-click-me", clicks[0].Id);
        Assert.True(clicks[0].Trusted);
    }

    [Fact]
    public async Task Press_enter_in_a_text_box_submits_its_form()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Act(obs, "Text Input", "press", "Enter");

        Assert.True(r.InputSent);
        var events = await s.Web.SettledAsync("controls.html");
        Assert.Contains(events, e => e.Type == "submit" && e.Id == "test-form");
        Assert.Contains(events, e => e.Type == "keydown" && e.Id == "input-text");
    }

    [Fact]
    public async Task Navigate_with_url_contains_is_confirmed()
    {
        var r = await s.Tool("browser_nav", new
        {
            action = "navigate",
            url = s.Web.Url("dynamic.html"),
            expect = Expect(new { kind = "url_contains", value = "dynamic.html" }),
        });

        Assert.Equal("confirmed", r.Status);
        Assert.True(r.InputSent);
    }

    [Fact]
    public async Task Element_appears_after_a_click_is_confirmed_and_the_new_element_is_in_the_page()
    {
        var obs = await s.Open("dynamic.html");
        var r = await s.Act(obs, "Add Element", "click", expect: Expect(new { kind = "element_appears", name_contains = "Newly Added" }));

        Assert.Equal("confirmed", r.Status);
        Assert.Contains("Newly Added", await s.Observe());
    }

    [Fact]
    public async Task All_of_and_any_of_are_confirmed_when_their_conditions_hold()
    {
        var obs = await s.Open("dynamic.html");
        var allOf = await s.Act(obs, "Add Element", "click", expect: Expect(new
        {
            kind = "all_of",
            items = new object[] { new { kind = "url_contains", value = "dynamic" }, new { kind = "element_appears", name_contains = "Newly Added" } },
        }));
        Assert.Equal("confirmed", allOf.Status);

        var anyOf = await s.Tool("browser_nav", new
        {
            action = "reload",
            expect = Expect(new
            {
                kind = "any_of",
                items = new object[] { new { kind = "text_appears", value = "no-such-text-xyz" }, new { kind = "url_contains", value = "dynamic" } },
            }),
        });
        Assert.Equal("confirmed", anyOf.Status);
    }

    [Fact]
    public async Task Delayed_reaction_is_confirmed_when_the_element_appears_in_time_and_times_out_when_it_does_not()
    {
        var obs = await s.Open("reaction.html");
        var later = await s.Act(obs, "Open later", "click", expect: Expect(new { kind = "element_appears", name_contains = "Appeared" }, 5000));
        Assert.Equal("confirmed", later.Status);

        obs = await s.Observe();
        var never = await s.Act(obs, "Do nothing", "click", expect: Expect(new { kind = "text_appears", value = "zzz-never" }, 400));
        Assert.Equal("timeout", never.Status);
        Assert.True(never.InputSent);
        // No automatic retry: the page saw one click on each button.
        var clicks = (await s.Web.SettledAsync("reaction.html")).Where(e => e.Type == "click").ToList();
        Assert.Equal(["open-later", "do-nothing"], clicks.Select(c => c.Id));
    }

    [Fact]
    public async Task Client_side_route_change_with_a_delayed_insert_is_confirmed_by_url_and_element()
    {
        var obs = await s.Open("route.html");
        var r = await s.Act(obs, "Go route", "click", expect: Expect(new
        {
            kind = "all_of",
            items = new object[] { new { kind = "url_contains", value = "/route/next" }, new { kind = "element_appears", name_contains = "Route Ready" } },
        }, 5000));

        Assert.Equal("confirmed", r.Status);
    }

    [Fact]
    public async Task W01_form_is_filled_and_submitted_and_the_servers_own_record_matches()
    {
        var obs = await s.Open("w01.html");
        Assert.Equal("confirmed", (await s.Act(obs, "Full text", "set_value", "Ada Lovelace")).Status);
        obs = await s.Observe();
        Assert.Equal("confirmed", (await s.Act(obs, "Email address", "set_value", "ada@example.invalid")).Status);
        obs = await s.Observe();
        Assert.Equal("confirmed", (await s.Act(obs, "Choice", "select", "beta")).Status);
        obs = await s.Observe();
        Assert.Equal("confirmed", (await s.Act(obs, "I agree", "check")).Status);
        obs = await s.Observe();
        Assert.Equal("confirmed", (await s.Act(obs, "Notes", "set_value", "line one")).Status);
        obs = await s.Observe();
        await s.Act(obs, "Continue", "click");

        await s.Web.SettledAsync();
        var record = Assert.Single(s.Web.Forms);
        using var json = JsonDocument.Parse(record);
        var root = json.RootElement;
        Assert.Equal("Ada Lovelace", root.GetProperty("text").GetString());
        Assert.Equal("ada@example.invalid", root.GetProperty("email").GetString());
        Assert.Equal("beta", root.GetProperty("choice").GetString());
        Assert.True(root.GetProperty("agree").GetBoolean());
        Assert.Equal("line one", root.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Large_page_truncates_and_viewport_only_lists_fewer_elements()
    {
        await s.Open("biglist.html");
        var truncated = await s.Observe(new { max_elements = 100 });
        Assert.Contains("truncated: ", truncated, StringComparison.Ordinal);
        Assert.Equal(100, truncated.Split('\n').Count(l => l.StartsWith("  b", StringComparison.Ordinal)));

        var viewport = await s.Observe(new { max_elements = 1000, viewport_only = true });
        var listed = viewport.Split('\n').Count(l => l.StartsWith("  b", StringComparison.Ordinal));
        Assert.InRange(listed, 1, 999);
        Assert.Matches(@"omitted: [1-9][0-9]* offscreen", viewport);
    }

    [Fact]
    public async Task Same_origin_frame_elements_have_a_frame_prefix_and_the_click_lands_inside_the_frame()
    {
        await s.Open("frames.html");

        // The inner frame finishes loading after the page. Observe again until its button is listed (bounded), instead of one
        // fixed sleep: on a busy PC (other test projects running at the same time) 600 ms was sometimes not enough.
        var obs = await s.Observe();
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!obs.Contains("Inner button", StringComparison.Ordinal) &&
               System.Diagnostics.Stopwatch.GetElapsedTime(waitStart) < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            obs = await s.Observe();
        }

        Assert.Contains("frames=2", obs, StringComparison.Ordinal);
        var line = obs.Split('\n').First(l => l.Contains("Inner button", StringComparison.Ordinal));
        Assert.Contains("(f2) ", line, StringComparison.Ordinal);

        s.Web.Clear();
        var r = await s.Act(obs, "Inner button", "click");

        Assert.True(r.InputSent);
        var clicks = (await s.Web.SettledAsync()).Where(e => e.Type == "click").ToList();
        Assert.Single(clicks);
        Assert.Equal("inner-btn", clicks[0].Id);
        Assert.EndsWith("frame-inner.html", clicks[0].Page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tab_expectations_window_opened_and_window_closed_work_for_browser_tabs()
    {
        var opened = await s.Tool("browser_nav", new
        {
            action = "new_tab",
            url = s.Web.Url("keys.html"),
            expect = Expect(new { kind = "window_opened", title_contains = "Keys Test Page" }, 8000),
        });
        Assert.Equal("confirmed", opened.Status);

        var tabs = (await s.Tool("browser_tabs", new { })).Text.Split('\n');
        var active = tabs.First(l => l.EndsWith(" active", StringComparison.Ordinal)).Split(' ')[1];
        var closed = await s.Tool("browser_nav", new
        {
            action = "close_tab",
            tab = active,
            expect = Expect(new { kind = "window_closed", window = active }, 8000),
        });
        Assert.Equal("confirmed", closed.Status);
        Assert.Equal("risky", closed.Tokens.TryGetValue("risk", out var risk) ? risk : null);   // close_tab is labelled Risky and still runs
    }
}
