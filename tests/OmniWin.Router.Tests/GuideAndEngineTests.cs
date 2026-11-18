using System.Text.Json;
using ModelContextProtocol.Protocol;
using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Engines;
using OmniWin.Server.Hosting;
using OmniWin.Server.Tools;

namespace OmniWin.Router.Tests;

/// <summary>The guide tool, the engine catalog and filter, and the way bridged results are reported (never confirmed).</summary>
public sealed class GuideAndEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ guide

    [Fact]
    public void Every_guide_topic_starts_with_the_server_and_contract_version()
    {
        foreach (var topic in GuideTools.Topics)
        {
            var text = GuideTools.OmniwinGuide(null, topic);
            Assert.StartsWith($"omniwin {ServerState.Version} contract={OmniWinContract.ContractVersion} guide={topic}\n", text, StringComparison.Ordinal);
            Assert.True(text.Length > 120, topic);
        }
    }

    [Fact]
    public void The_default_topic_is_start_and_an_unknown_topic_lists_the_real_ones()
    {
        Assert.Contains("guide=start", GuideTools.OmniwinGuide(null, null), StringComparison.Ordinal);
        var unknown = GuideTools.OmniwinGuide(null, "nonsense");
        Assert.Contains("unknown topic", unknown, StringComparison.Ordinal);
        Assert.Contains("recovery", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recovery_topic_explains_every_retry_word()
    {
        var text = GuideTools.OmniwinGuide(null, "recovery");
        foreach (var word in RetryAdvice.All) Assert.Contains("retry=" + word, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fallback_tools_listing_covers_the_attributed_tools_and_not_the_optional_ones()
    {
        var text = GuideTools.OmniwinGuide(null, "tools");
        foreach (var tool in new[] { "desktop_act", "desktop_observe", "omniwin_guide", "omniwin_do", "engines_list", "media", "audio", "window_manage" })
        {
            Assert.Contains("- " + tool + "(", text, StringComparison.Ordinal);
        }

        // engine_call and the power tools are registered only when switched on, so the default listing does not show them.
        Assert.DoesNotContain("- engine_call(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- shell(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- registry(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tools_topic_lists_exactly_the_tools_it_is_given_so_it_cannot_describe_another_build()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"engine":{"type":"string"},"args":{"type":"object"}},"required":["engine"]}""").RootElement;
        var tools = new List<Tool>
        {
            new() { Name = "engine_call", Description = "Forward one call. More text.", InputSchema = schema },
            new() { Name = "omniwin_guide", Description = "Read a guide.", InputSchema = schema, Annotations = new ToolAnnotations { ReadOnlyHint = true } },
        };

        var text = GuideTools.Render("tools", tools);

        Assert.Contains("- engine_call(engine, args?): Forward one call.", text, StringComparison.Ordinal);
        Assert.Contains("- omniwin_guide(engine, args?) [read-only]: Read a guide.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("desktop_act", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ engine catalog

    private static string FakeToolsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "omniwin-test-engines-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "windows-mcp", ".venv", "Scripts"));
        File.WriteAllText(Path.Combine(root, "windows-mcp", ".venv", "Scripts", "windows-mcp.exe"), "x");
        File.WriteAllText(Path.Combine(root, "windows-mcp", "config.toml"), "# test");
        Directory.CreateDirectory(Path.Combine(root, "agent-desktop"));
        File.WriteAllText(Path.Combine(root, "agent-desktop", "agent-desktop.exe"), "x");
        return root;
    }

    private static void RemoveOwn(string folder)
    {
        // The test created this folder itself; it is the only folder this test removes.
        if (Path.GetFileName(folder).StartsWith("omniwin-test-engines-", StringComparison.Ordinal) && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void With_power_tools_off_Windows_MCP_starts_without_telemetry_control_and_its_command_tools()
    {
        var tools = FakeToolsFolder();
        try
        {
            var engines = EngineCatalog.Build(new OmniWinConfig { Engines = new EnginesConfig { ToolsDir = tools } }, AppContext.BaseDirectory);

            var win = Assert.Single(engines, e => e.Name == "windows-mcp");
            Assert.Equal("false", win.Env["ANONYMIZED_TELEMETRY"]);
            Assert.Contains("--no-control", win.Args);
            var exclude = win.Args[win.Args.ToList().IndexOf("--exclude-tools") + 1];
            foreach (var tool in new[] { "PowerShell", "Registry", "FileSystem", "Process" }) Assert.Contains(tool, exclude, StringComparison.Ordinal);
            Assert.Equal(["PowerShell", "Registry", "FileSystem", "Process"], win.ExcludeTools);
            Assert.DoesNotContain("install", win.Args);   // never the scheduled-task installer

            var agent = Assert.Single(engines, e => e.Name == "agent-desktop");
            Assert.Equal(["mcp", "--session", "omniwin"], agent.Args);
            Assert.DoesNotContain("--headed", agent.Args);   // headless unless the owner adds it
        }
        finally
        {
            RemoveOwn(tools);
        }
    }

    [Fact]
    public void With_power_tools_on_the_filter_is_lifted()
    {
        var tools = FakeToolsFolder();
        try
        {
            var config = new OmniWinConfig { PowerTools = new PowerToolsConfig { Enabled = true }, Engines = new EnginesConfig { ToolsDir = tools } };

            var win = Assert.Single(EngineCatalog.Build(config, AppContext.BaseDirectory), e => e.Name == "windows-mcp");

            Assert.Empty(win.ExcludeTools);
            Assert.DoesNotContain("--exclude-tools", win.Args);
        }
        finally
        {
            RemoveOwn(tools);
        }
    }

    [Fact]
    public void Engines_off_or_no_tools_folder_means_no_engines()
    {
        Assert.Empty(EngineCatalog.Build(new OmniWinConfig { Engines = new EnginesConfig { Enabled = false } }, AppContext.BaseDirectory));
        var empty = Path.Combine(Path.GetTempPath(), "omniwin-test-engines-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Empty(EngineCatalog.Build(new OmniWinConfig { Engines = new EnginesConfig { ToolsDir = empty } }, AppContext.BaseDirectory));
        }
        finally
        {
            RemoveOwn(empty);
        }
    }

    [Fact]
    public void An_exclude_list_always_wins_over_an_allow_list()
    {
        var host = new EngineHost(new EngineConfig
        {
            Name = "x", Command = "x.exe", ExcludeTools = ["Registry"], AllowTools = ["Registry", "Click"],
        });

        Assert.True(host.IsAllowed("Click"));
        Assert.True(host.IsAllowed("click"));
        Assert.False(host.IsAllowed("Registry"));
        Assert.False(host.IsAllowed("Snapshot"));   // not on the allow list
    }

    [Fact]
    public async Task A_filtered_tool_is_denied_before_the_engine_is_even_started()
    {
        // The command is not a real engine: the call must be refused by the filter and never reach a start.
        var engine = new EngineConfig { Name = "windows-mcp", Command = "does-not-exist.exe", ExcludeTools = ["PowerShell"] };
        var manager = new EngineManager(new OmniWinConfig { Engines = new EnginesConfig { List = [engine] } }, new AllowGate(), new FreeKill(), new NullMetrics());

        var outcome = await manager.CallAsync("windows-mcp", "PowerShell", JsonDocument.Parse("""{"command":"Get-Date"}""").RootElement, Ct);

        Assert.Equal(ActStatus.Denied, outcome.Result.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, outcome.Result.Reason);
        Assert.False(outcome.Result.InputSent);
        Assert.Contains("power tools are off", outcome.Result.Detail, StringComparison.Ordinal);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task An_unknown_engine_and_an_empty_call_are_refused_with_nothing_sent()
    {
        var manager = new EngineManager(new OmniWinConfig { Engines = new EnginesConfig { Enabled = true } }, new AllowGate(), new FreeKill(), new NullMetrics());

        var unknown = await manager.CallAsync("nope", "Click", null, Ct);
        var empty = await manager.CallAsync("", "", null, Ct);

        Assert.Equal(ReasonCodes.NotFound, unknown.Result.Reason);
        Assert.Equal(ReasonCodes.UnsupportedAction, empty.Result.Reason);
        Assert.False(unknown.Result.InputSent);
        Assert.False(empty.Result.InputSent);
    }

    [Fact]
    public async Task The_safety_gate_can_stop_an_engine_call()
    {
        var engine = new EngineConfig { Name = "windows-mcp", Command = "does-not-exist.exe" };
        var manager = new EngineManager(new OmniWinConfig { Engines = new EnginesConfig { List = [engine] } }, new AllowGate { Deny = true }, new FreeKill(), new NullMetrics());

        var outcome = await manager.CallAsync("windows-mcp", "Click", null, Ct);

        Assert.Equal(ActStatus.Denied, outcome.Result.Status);
    }

    [Fact]
    public void With_power_tools_off_agent_desktop_cannot_launch_batch_force_close_or_run_a_sandbox()
    {
        var tools = FakeToolsFolder();
        try
        {
            var agent = Assert.Single(EngineCatalog.Build(new OmniWinConfig { Engines = new EnginesConfig { ToolsDir = tools } }, AppContext.BaseDirectory),
                e => e.Name == "agent-desktop");

            foreach (var tool in new[] { "desktop_launch", "desktop_batch", "desktop_close_app", "desktop_trace_export", "desktop_sandbox_exec", "desktop_sandbox_create" })
                Assert.Contains(tool, agent.ExcludeTools);

            // Reading and ordinary control stay.
            foreach (var tool in new[] { "desktop_snapshot", "desktop_click", "desktop_type", "desktop_focus_window", "desktop_list_windows" })
                Assert.DoesNotContain(tool, agent.ExcludeTools);

            Assert.Contains("desktop_snapshot", agent.ReadOnlyTools);
            Assert.Contains("desktop_list_displays", agent.ReadOnlyTools);
            Assert.DoesNotContain("desktop_click", agent.ReadOnlyTools);
        }
        finally
        {
            RemoveOwn(tools);
        }
    }

    [Fact]
    public void With_power_tools_off_Windows_MCP_cannot_start_a_program_or_write_a_file_through_an_argument()
    {
        var tools = FakeToolsFolder();
        try
        {
            var win = Assert.Single(EngineCatalog.Build(new OmniWinConfig { Engines = new EnginesConfig { ToolsDir = tools } }, AppContext.BaseDirectory),
                e => e.Name == "windows-mcp");

            foreach (var key in new[] { "App.executable", "App.args", "App.cwd", "Screenshot.save_path", "Snapshot.save_path" })
                Assert.Contains(key, win.ExcludeArguments);

            var on = Assert.Single(EngineCatalog.Build(
                new OmniWinConfig { PowerTools = new PowerToolsConfig { Enabled = true }, Engines = new EnginesConfig { ToolsDir = tools } }, AppContext.BaseDirectory),
                e => e.Name == "windows-mcp");
            Assert.Empty(on.ExcludeArguments);
        }
        finally
        {
            RemoveOwn(tools);
        }
    }

    [Fact]
    public void An_excluded_argument_is_found_whatever_its_other_arguments_and_a_null_value_does_not_count()
    {
        var host = new EngineHost(new EngineConfig { Name = "x", Command = "x.exe", ExcludeArguments = ["App.executable", "App.args"] });

        Assert.Equal("App.executable", host.RefusedArgument("App", JsonDocument.Parse("""{"name":"notepad","executable":"powershell.exe"}""").RootElement));
        Assert.Equal("App.args", host.RefusedArgument("app", JsonDocument.Parse("""{"args":"-c x"}""").RootElement));
        Assert.Null(host.RefusedArgument("App", JsonDocument.Parse("""{"name":"notepad","mode":"launch"}""").RootElement));
        Assert.Null(host.RefusedArgument("App", JsonDocument.Parse("""{"executable":null}""").RootElement));
        Assert.Null(host.RefusedArgument("Click", JsonDocument.Parse("""{"executable":"x"}""").RootElement));
        Assert.Null(host.RefusedArgument("App", null));
    }

    [Fact]
    public async Task A_call_with_an_excluded_argument_is_denied_before_the_engine_is_started()
    {
        var engine = new EngineConfig { Name = "windows-mcp", Command = "does-not-exist.exe", ExcludeArguments = ["App.executable"] };
        var manager = new EngineManager(new OmniWinConfig { Engines = new EnginesConfig { List = [engine] } }, new AllowGate(), new FreeKill(), new NullMetrics());

        var outcome = await manager.CallAsync("windows-mcp", "App", JsonDocument.Parse("""{"mode":"launch","executable":"powershell.exe"}""").RootElement, Ct);

        Assert.Equal(ActStatus.Denied, outcome.Result.Status);
        Assert.False(outcome.Result.InputSent);
        Assert.Contains("App.executable", outcome.Result.Detail, StringComparison.Ordinal);
        await manager.DisposeAsync();
    }

    // ------------------------------------------------------------------ never confirmed

    [Theory]
    [InlineData(false, "status=unverified input_sent=false", "retry=safe")]
    [InlineData(true, "status=unverified input_sent=true", "retry=check_state")]
    public void A_bridged_result_is_unverified_and_says_whether_input_may_have_been_sent(bool inputSent, string lead, string retry)
    {
        var outcome = new EngineCallOutcome(new ActResult(ActStatus.Unverified, inputSent, null, "from the engine", null, null, Timing.Total(5)),
            [new TextContentBlock { Text = "engine said hello" }]);

        var result = EngineManager.ToToolResult("windows-mcp", "Snapshot", outcome);

        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.StartsWith(lead, text, StringComparison.Ordinal);
        Assert.Contains(retry, text, StringComparison.Ordinal);
        Assert.Contains("engine=windows-mcp tool=Snapshot", text, StringComparison.Ordinal);
        Assert.Contains("untrusted engine content", text, StringComparison.Ordinal);
        Assert.Contains("engine said hello", text, StringComparison.Ordinal);
        Assert.DoesNotContain("status=confirmed", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ audit metadata

    [Fact]
    public void The_audit_line_names_the_engine_and_tool_but_never_the_arguments()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["engine"] = JsonDocument.Parse("\"windows-mcp\"").RootElement.Clone(),
            ["tool"] = JsonDocument.Parse("\"Click\"").RootElement.Clone(),
            ["args"] = JsonDocument.Parse("""{"loc":[10,20],"secret":"hunter2"}""").RootElement.Clone(),
        };

        var json = ToolCallAudit.Redact(args);

        Assert.Contains("windows-mcp", json, StringComparison.Ordinal);
        Assert.Contains("Click", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loc", json, StringComparison.Ordinal);
    }
}
