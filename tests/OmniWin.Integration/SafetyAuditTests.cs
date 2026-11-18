using System.Text.Json;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// Group 5 (WS4 brief §4): safety, audit and redaction. Each test gets its own server so audit lines can be counted exactly
/// and every file the server wrote can be searched after it has stopped.
/// </summary>
public sealed class SafetyAuditTests
{
    private const string PasswordSecret = "OMNIWIN_PASSWORD_SECRET";
    private const string CookieSecret = "OMNIWIN_COOKIE_SECRET";
    private const string StorageSecret = "OMNIWIN_STORAGE_SECRET";
    private const string TypedPassword = "OMNIWIN_TYPED_PASSWORD";
    private const string TypedNote = "OMNIWIN_TYPED_NOTE";
    private const string QuerySecret = "OMNIWIN_URL_QUERY_SECRET";
    private const string FragmentSecret = "OMNIWIN_URL_FRAGMENT_SECRET";

    [Fact]
    public Task One_audit_line_per_tool_call_with_redacted_arguments_and_the_step_id() => E2ESession.UsingAsync(async s =>
    {
        var calls = 0;
        async Task<ToolResult> Call(string tool, object args)
        {
            calls++;
            return await s.Tool(tool, args);
        }

        await Call("omniwin_status", new { });
        var nav = await Call("browser_nav", new { action = "navigate", url = s.Web.Url("controls.html") + "?token=" + QuerySecret + "#" + FragmentSecret });
        Assert.True(nav.InputSent);
        calls++;
        var obs = (await s.Tool("browser_observe", new { })).Text;
        var set = await Call("browser_act", new { @ref = E2ESession.RefOf(obs, "Text Input"), gen = E2ESession.GenOf(obs), action = "set_value", value = TypedNote });
        await Call("browser_tabs", new { });
        await Call("system_info", new { kind = "time" });
        await Call("apps_list", new { filter = "notepad" });

        var entries = await s.AuditAsync(calls);

        Assert.Equal(calls, entries.Count);   // exactly one line per tool call
        var nav2 = entries.Single(e => e.GetProperty("tool").GetString() == "browser_nav");
        Assert.Equal("{\"action\":\"navigate\",\"url\":\"http://127.0.0.1\"}", nav2.GetProperty("args_redacted_json").GetString());   // scheme and host only
        var act = entries.Single(e => e.GetProperty("tool").GetString() == "browser_act");
        var args = JsonDocument.Parse(act.GetProperty("args_redacted_json").GetString()!).RootElement;
        Assert.Matches(@"^b[0-9]+$", args.GetProperty("ref").GetString());   // bN refs are kept
        Assert.Equal("set_value", args.GetProperty("action").GetString());
        Assert.Equal($"<{TypedNote.Length} chars>", args.GetProperty("value").GetString());   // typed text logged as a length
        Assert.Equal(set.Tokens["step"], act.GetProperty("step_id").GetString());   // the audit line sees the run-log step id
        Assert.True(act.GetProperty("input_sent").GetBoolean());
    });

    [Fact]
    public Task Secrets_and_page_controlled_urls_appear_in_no_log_audit_stderr_or_stdout_where_they_must_not() => E2ESession.UsingAsync(async s =>
    {
        var obs = await s.Open("secrets.html", "?token=" + QuerySecret + "#" + FragmentSecret);
        Assert.DoesNotContain(PasswordSecret, obs, StringComparison.Ordinal);
        Assert.Contains("value=<redacted>", obs, StringComparison.Ordinal);

        var note = await s.Act(obs, "Note", "set_value", TypedNote);
        Assert.Equal("confirmed", note.Status);
        obs = await s.Observe();
        var pw = await s.Act(obs, "\"Password\"", "type", TypedPassword);
        Assert.True(pw.InputSent);
        obs = await s.Observe();
        await s.Act(obs, "\"Password\"", "press", "Enter");
        await s.Tool("browser_tabs", new { });

        await s.AuditAsync(7);
        var stderr = await s.Server.StopAsync();
        var written = s.AllWrittenText() + "\n" + stderr;
        var stdout = string.Join('\n', s.Server.StdoutLines);

        // Nothing the page keeps secret, and nothing typed into a password field, appears anywhere OmniWin wrote or printed.
        foreach (var secret in new[] { PasswordSecret, CookieSecret, StorageSecret, TypedPassword })
        {
            Assert.DoesNotContain(secret, written, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, stdout, StringComparison.Ordinal);
        }

        // Page-controlled URL parts (query, fragment) and typed text never reach the logs or audit file.
        foreach (var secret in new[] { QuerySecret, FragmentSecret, TypedNote })
        {
            Assert.DoesNotContain(secret, written, StringComparison.Ordinal);
        }

        // Contract §9.1: the tab line in a tool result does carry the page URL for the model; that is not a log.
        Assert.Contains(QuerySecret, stdout, StringComparison.Ordinal);
    });

    [Fact]
    public Task Audit_file_never_contains_image_or_clipboard_text_and_denied_calls_are_logged_with_their_reason() => E2ESession.UsingAsync(async s =>
    {
        var obs = await s.Open("delete.html");
        var denied = await s.Act(obs, "Delete account", "click");
        Assert.Equal("denied", denied.Status);

        var entries = await s.AuditAsync(3);
        var entry = entries.Last(e => e.GetProperty("tool").GetString() == "browser_act");

        Assert.Equal("denied", entry.GetProperty("status").GetString());
        Assert.Equal("policy_forbidden", entry.GetProperty("reason").GetString());
        Assert.False(entry.GetProperty("input_sent").GetBoolean());
        Assert.False(entry.TryGetProperty("step_id", out _));   // a denied act is not a recorded step
    });

    [Fact]
    public Task Stdout_guard_and_clean_shutdown_after_a_busy_session() => E2ESession.UsingAsync(async s =>
    {
        var obs = await s.Open("controls.html");
        await s.Act(obs, "Click Me", "click");
        await s.Tool("windows_list", new { });
        var stderr = await s.Server.StopAsync();

        Assert.NotEmpty(s.Server.StdoutLines);
        Assert.All(s.Server.StdoutLines, line => Assert.Equal("2.0", JsonDocument.Parse(line).RootElement.GetProperty("jsonrpc").GetString()));
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
    });

    // Finding 3, resolved by owner policy (nothing is blocked except deleting): rate_limit is accepted in config but ignored,
    // so heavy repeated calls are never refused with rate_limit. 150 read-only calls is five times the old default of 30 per 10 s.
    [Fact]
    public Task Heavy_repeated_calls_are_not_rate_limited() => E2ESession.UsingAsync(async s =>
    {
        var refused = 0;
        for (var i = 0; i < 150; i++)
        {
            var result = await s.Tool("system_info", new { kind = "time" });
            if (result.FirstLine.Contains("rate_limit", StringComparison.Ordinal)) refused++;
        }

        Assert.Equal(0, refused);
        var status = await s.Tool("omniwin_status", new { });
        Assert.DoesNotContain("rate_limit=", status.FirstLine, StringComparison.Ordinal);
    });
}
