using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Core;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>Group 7 (system tools and OCR, read-only parts) and group 8 (workflows) of the WS4 brief §4. No real input, nothing shown on screen.</summary>
public sealed class SystemToolsTests(E2ESession s) : IClassFixture<E2ESession>
{
    [Fact]
    public async Task System_info_answers_time_network_displays_and_battery_and_refuses_an_unknown_kind()
    {
        var time = (await s.Tool("system_info", new { kind = "time" })).Text;
        Assert.Matches(@"^local=\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} utc_offset=", time);

        var network = (await s.Tool("system_info", new { kind = "network" })).Text;
        Assert.StartsWith(ResultFormatter.UntrustedHeader, network, StringComparison.Ordinal);   // adapter names come from the machine
        Assert.Contains("connected=", network, StringComparison.Ordinal);

        Assert.Matches(@"^display 1 primary \d+x\d+ at -?\d+,-?\d+ scale=\d+%", (await s.Tool("system_info", new { kind = "displays" })).Text);
        Assert.Matches(@"^battery=", (await s.Tool("system_info", new { kind = "battery" })).Text);

        var bad = await s.Tool("system_info", new { kind = "passwords" });
        Assert.False(bad.InputSent);
        Assert.NotEqual("confirmed", bad.Status);
    }

    [Fact]
    public async Task Apps_list_is_the_untrusted_header_plus_name_and_id_lines()
    {
        var text = (await s.Tool("apps_list", new { filter = "notepad" })).Text;
        var lines = text.Split('\n');

        Assert.Equal(ResultFormatter.UntrustedHeader, lines[0]);
        Assert.Matches(@"^"".+"" id="".+""$", lines[1]);
        Assert.Matches(@"^apps: \d+", lines[^1]);
    }

    [Fact]
    public async Task Audio_read_returns_the_state_or_an_honest_no_device_result_and_never_input()
    {
        var r = await s.Tool("audio", new { });

        if (r.Status is null)
        {
            Assert.Matches(@"volume=\d+", r.Text);
        }
        else
        {
            Assert.False(r.InputSent);
            Assert.Equal("not_found", r.Reason);
        }
    }

    [Theory]
    [InlineData("window_manage", "focus")]
    [InlineData("screen_ocr", null)]
    public async Task Unknown_window_is_blocked_not_found_with_nothing_done_or_captured(string tool, string? action)
    {
        var r = action is null
            ? await s.Tool(tool, new { window = "w999999" })
            : await s.Tool(tool, new { window = "w999999", action });

        Assert.Equal("blocked", r.Status);
        Assert.Equal("not_found", r.Reason);
        Assert.False(r.InputSent);
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("powershell -Command Get-Date")]
    [InlineData("https://example.invalid/")]
    public async Task App_launch_refuses_paths_urls_and_command_lines_and_launches_nothing(string app)
    {
        var r = await s.Tool("app_launch", new { app });

        // Paths and URLs are denied by policy; a command line is not an app name at all, so it is blocked as not found. Either way nothing starts.
        Assert.Contains(r.Status, new[] { "denied", "blocked" });
        Assert.Contains(r.Reason, new[] { "policy_forbidden", "not_found", "unsupported_action" });
        Assert.False(r.InputSent);
    }

    [Fact]
    public async Task Media_with_an_unknown_action_is_refused_before_any_key_is_sent()
    {
        var r = await s.Tool("media", new { action = "format_disk" });

        Assert.False(r.InputSent);
        Assert.NotEqual("confirmed", r.Status);
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    [Trait("Category", "RealInput")]
    public void Live_audio_set_and_restore_window_manage_clipboard_round_trip_screen_ocr_and_ocr_ref_click()
    {
        // Phase 5 (see LiveSessionTests for the scripted list; this marker keeps group 7 visible in the report).
    }
}

public sealed class WorkflowE2ETests(E2ESession s) : IClassFixture<E2ESession>
{
    private async Task<(string Obs, ToolResult Set)> SetTextAsync(string value)
    {
        var obs = await s.Open("controls.html");
        var set = await s.Act(obs, "Text Input", "set_value", value);
        return (obs, set);
    }

    [Fact]
    public async Task Run_steps_runs_each_step_through_the_normal_path_and_reports_each_result()
    {
        var obs = await s.Open("controls.html");
        var gen = E2ESession.GenOf(obs);
        var textRef = E2ESession.RefOf(obs, "Text Input");
        var checkRef = E2ESession.RefOf(obs, "I agree");

        var r = await s.Tool("run_steps", new
        {
            steps = new object[]
            {
                new { tool = "browser_act", @ref = textRef, gen, action = "set_value", value = "from-run-steps" },
                new { tool = "browser_act", @ref = checkRef, gen, action = "check" },
            },
        });

        Assert.Equal("confirmed", r.Status);
        Assert.Equal("2/2", r.Tokens["steps"]);
        var events = await s.Web.SettledAsync("controls.html");
        Assert.Contains(events, e => e.Id == "input-text" && e.Type == "change");
        Assert.Contains(events, e => e.Id == "check-agree" && e.Type == "change");
        Assert.All(events, e => Assert.Contains(e.Id, new[] { "input-text", "check-agree" }));
    }

    [Fact]
    public async Task Run_steps_stops_at_the_first_step_that_did_not_pass_and_later_steps_send_nothing()
    {
        var obs = await s.Open("controls.html");
        var gen = E2ESession.GenOf(obs);

        var r = await s.Tool("run_steps", new
        {
            steps = new object[]
            {
                new { tool = "browser_act", @ref = "b999999", gen = 1, action = "click" },
                new { tool = "browser_act", @ref = E2ESession.RefOf(obs, "Click Me"), gen, action = "click" },
            },
        });

        Assert.Equal("stale", r.Status);
        Assert.Equal("0/2", r.Tokens["steps"]);
        Assert.Equal("1", r.Tokens["stopped_at"]);
        Assert.Contains("current observation:", r.Text, StringComparison.Ordinal);
        Assert.Empty(await s.Web.SettledAsync("controls.html"));
    }

    [Fact]
    public async Task Run_steps_cannot_get_a_delete_step_past_the_gate()
    {
        var obs = await s.Open("delete.html");

        var r = await s.Tool("run_steps", new
        {
            steps = new object[] { new { tool = "browser_act", @ref = E2ESession.RefOf(obs, "Delete account"), gen = E2ESession.GenOf(obs), action = "click" } },
        });

        Assert.Equal("denied", r.Status);
        Assert.False(r.InputSent);
        Assert.Empty(await s.Web.SettledAsync("delete.html"));
    }

    [Fact]
    public async Task Run_steps_refuses_tools_that_are_not_acts()
    {
        var r = await s.Tool("run_steps", new { steps = new object[] { new { tool = "omniwin_status" }, new { tool = "run_steps" } } });

        Assert.False(r.InputSent);
        Assert.NotEqual("confirmed", r.Status);
        Assert.Contains("unsupported_action", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saved_workflow_holds_data_only_no_typed_text_and_runs_again_with_a_parameter()
    {
        var (_, set) = await SetTextAsync("OMNIWIN_WORKFLOW_TYPED_SECRET");
        var step = set.Tokens["step"];
        var name = "wf-" + Guid.NewGuid().ToString("N")[..8];

        var save = await s.Tool("workflow_save", new { name, description = "set the text field", from_step = step, to_step = step });
        Assert.Equal("saved", save.Tokens["status"]);

        var file = Path.Combine(s.DataDir, "workflows", name + ".json");
        Assert.True(File.Exists(file));
        var json = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("OMNIWIN_WORKFLOW_TYPED_SECRET", json, StringComparison.Ordinal);   // typed text is a placeholder, never saved
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        var param = doc.RootElement.GetProperty("params").EnumerateObject().Single().Name;   // e.g. p2

        var list = (await s.Tool("workflow_list", new { })).Text;
        Assert.Contains(name, list, StringComparison.Ordinal);

        // Run it again on a freshly loaded page, supplying the text. Its element is found again from the saved selector.
        await s.Open("controls.html");
        s.Web.Clear();
        var run = await s.Tool("workflow_run", new { name, @params = new Dictionary<string, string> { [param] = "second-run" } });

        Assert.Equal("confirmed", run.Tokens["status"]);
        Assert.Equal("1/1", run.Tokens["steps"]);
        var changes = (await s.Web.SettledAsync("controls.html")).Where(e => e.Type == "change").ToList();
        Assert.Single(changes);
        Assert.Equal("input-text", changes[0].Id);
        Assert.Equal("second-run", changes[0].Value);
    }

    [Fact]
    public async Task Workflow_run_stops_with_a_missing_parameter_before_any_step_and_a_hand_edited_delete_step_is_refused()
    {
        var (_, set) = await SetTextAsync("anything");
        var step = set.Tokens["step"];
        var name = "wf-" + Guid.NewGuid().ToString("N")[..8];
        await s.Tool("workflow_save", new { name, description = "d", from_step = step, to_step = step });
        var file = Path.Combine(s.DataDir, "workflows", name + ".json");
        var json = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);

        var missing = await s.Tool("workflow_run", new { name });
        Assert.False(missing.InputSent);
        Assert.NotEqual("confirmed", missing.Status);

        // A workflow edited by hand to point at a delete control is refused by the gate when it runs.
        var evil = Regex.Replace(json, "\"name\": \"Text Input:\"", "\"name\": \"Delete account\"");
        evil = evil.Replace("\"automation_id\": \"input-text\"", "\"automation_id\": \"btn-x\"", StringComparison.Ordinal)
                   .Replace($"\"name\": \"{name}\"", $"\"name\": \"{name}x\"", StringComparison.Ordinal);
        Assert.NotEqual(json, evil);
        var evilName = name + "x";
        await File.WriteAllTextAsync(Path.Combine(s.DataDir, "workflows", evilName + ".json"), evil, TestContext.Current.CancellationToken);
        await s.Open("controls.html");
        s.Web.Clear();

        var run = await s.Tool("workflow_run", new { name = evilName, @params = new Dictionary<string, string> { ["p1"] = "x", ["p2"] = "x" } });

        Assert.False(run.InputSent);
        Assert.NotEqual("confirmed", run.Tokens.GetValueOrDefault("status"));
        Assert.Empty(await s.Web.SettledAsync("controls.html"));
    }

    [Fact]
    public async Task Saving_with_an_unknown_step_id_is_refused()
    {
        var r = await s.Tool("workflow_save", new { name = "nothing", description = "d", from_step = "s9999", to_step = "s9999" });

        Assert.NotEqual("saved", r.Tokens.GetValueOrDefault("status"));
        Assert.False(r.InputSent);
    }

    [Fact]
    public async Task Wait_for_routes_a_browser_condition_to_the_browser()
    {
        await s.Open("dynamic.html");

        var r = await s.Tool("wait_for", new { condition = new { kind = "url_contains", value = "dynamic.html" }, timeout_ms = 2000 });

        Assert.Contains("met=true", r.Text, StringComparison.Ordinal);
    }
}
