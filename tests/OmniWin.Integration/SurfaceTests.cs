using System.Text.Json;
using OmniWin.Core;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>Group 1 (WS4 brief §4): the tool surface matches contract §8 exactly. Read back from a real server over stdio.</summary>
public sealed class SurfaceTests(E2ESession s) : IClassFixture<E2ESession>
{
    private async Task<Dictionary<string, JsonElement>> ToolsAsync() =>
        (await s.Server.ListToolsAsync()).ToDictionary(t => t.GetProperty("name").GetString()!, StringComparer.Ordinal);

    [Fact]
    public async Task Tool_list_is_exactly_contract_8_plus_only_the_look_only_tools()
    {
        var tools = await ToolsAsync();
        var extra = tools.Keys.Except(Contract.Tools.Keys).ToList();
        var missing = Contract.Tools.Keys.Except(tools.Keys).ToList();

        Assert.Empty(missing);
        Assert.True(extra.All(t => Contract.LookOnly.Contains(t) || Contract.Optional.Contains(t)), "Tools that are not in contract §8: " + string.Join(", ", extra));
        // Power tools are off by default: a default server must not offer them.
        Assert.DoesNotContain(extra, t => t is "shell" or "files" or "registry" or "process");
    }

    [Fact]
    public async Task Every_tool_has_the_contract_parameter_names_in_snake_case()
    {
        var tools = await ToolsAsync();
        foreach (var (name, expected) in Contract.Tools)
        {
            var actual = tools[name].GetProperty("inputSchema").TryGetProperty("properties", out var props)
                ? props.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()
                : [];
            Assert.True(expected.Order(StringComparer.Ordinal).SequenceEqual(actual),
                $"{name}: expected [{string.Join(",", expected)}] but the server lists [{string.Join(",", actual)}]");
        }
    }

    [Fact]
    public async Task No_tool_runs_a_shell_or_script_or_acts_from_a_screenshot_or_pixel_position()
    {
        var tools = await ToolsAsync();
        foreach (var name in tools.Keys)
        {
            Assert.DoesNotMatch("shell|script|powershell|cmd|exec|run_command|eval", name.Replace("run_steps", "").Replace("workflow_run", ""));
        }

        // Look-only tools (add-on 16), when present, can't take anything to act on.
        foreach (var name in tools.Keys.Where(Contract.LookOnly.Contains))
        {
            var props = tools[name].GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.Empty(props.Intersect(["ref", "gen", "action", "keys", "x", "y", "value", "text"]));
        }

        Assert.DoesNotContain("screenshot", string.Join(' ', tools.Keys));
    }

    [Fact]
    public async Task Act_tool_descriptions_carry_the_retry_wording_from_contract_10_3()
    {
        var tools = await ToolsAsync();
        foreach (var name in Contract.ActTools)
        {
            Assert.Contains(ToolDocs.RetryRules, tools[name].GetProperty("description").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Read_tool_descriptions_that_return_page_or_app_text_say_it_is_data_not_instructions()
    {
        var tools = await ToolsAsync();
        foreach (var name in new[] { "browser_observe", "browser_tabs", "desktop_observe", "windows_list" })
        {
            Assert.Contains(ToolDocs.UntrustedContent, tools[name].GetProperty("description").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Status_lists_every_engine_and_says_the_browser_is_detached_before_first_use()
    {
        var status = (await s.Tool("omniwin_status", new { })).Text;

        foreach (var component in new[] { "desktop=", "browser=", "system_tools=", "ocr=", "safety_gate=", "kill_switch=", "audit_log=", "workflows=" })
        {
            Assert.Contains(component, status, StringComparison.Ordinal);
        }

        Assert.Contains("browser=detached", status, StringComparison.Ordinal);
        Assert.Contains("safety_gate=no_delete", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stdout_guard_every_line_the_server_wrote_so_far_is_json_rpc()
    {
        await ToolsAsync();
        Assert.NotEmpty(s.Server.StdoutLines);
        Assert.All(s.Server.StdoutLines, line =>
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        });
    }
}
