using System.Text.Json;
using OmniWin.Core;
using OmniWin.Server.Hosting;

namespace OmniWin.Integration;

/// <summary>2.4: audit lines for the browser and look-only view tools carry metadata, never page text, full URLs or image data.</summary>
public sealed class ToolCallAuditViewTests
{
    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void View_tool_arguments_log_window_size_and_full_page_metadata_only()
    {
        var redacted = ToolCallAudit.Redact(Args("""{"window":"screen","max_side":1280,"full_page":true,"tab":"t2","region":"10,10,50,50"}"""));

        using var doc = JsonDocument.Parse(redacted);
        Assert.Equal("t2", doc.RootElement.GetProperty("tab").GetString());
        Assert.Contains("1280", redacted);
        Assert.DoesNotContain("10,10,50,50", redacted);   // free text such as a region is not logged as given
    }

    [Fact]
    public void Browser_url_is_logged_as_scheme_and_host_only()
    {
        var redacted = ToolCallAudit.Redact(Args("""{"action":"navigate","url":"https://example.com/private/path?token=SECRET#frag"}"""));

        Assert.Contains("https://example.com", redacted);
        Assert.DoesNotContain("private", redacted);
        Assert.DoesNotContain("SECRET", redacted);
        Assert.DoesNotContain("frag", redacted);
    }

    [Fact]
    public void Browser_refs_and_tab_ids_are_kept_and_other_values_are_not()
    {
        var redacted = ToolCallAudit.Redact(Args("""{"ref":"b12","gen":4,"action":"type","value":"hunter2","tab":"t3"}"""));

        Assert.Contains("b12", redacted);
        Assert.Contains("t3", redacted);
        Assert.DoesNotContain("hunter2", redacted);
    }

    [Fact]
    public void An_audit_entry_built_from_a_view_result_line_holds_no_image_data()
    {
        // The filter reads only the first line of the first text block; the image block is never read.
        var line = "view tool=screen_view window=w3 size=1280x720 format=jpeg";
        var entry = ToolCallAudit.BuildEntry(DateTimeOffset.UtcNow, "screen_view", "{}", line, false, 12);

        var json = JsonSerializer.Serialize(entry, CoreJson.Options);
        Assert.True(json.Length < 400, json);
        Assert.DoesNotContain("base64", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- F7: harmless details are logged plainly; typed text, values and keys stay redacted ----

    [Fact]
    public void App_launch_system_and_window_manage_details_are_logged_plainly()
    {
        var launch = ToolCallAudit.Redact(Args("""{"app":"Calculator","args":"--flag secret"}"""));
        Assert.Contains("Calculator", launch);
        Assert.DoesNotContain("secret", launch);

        Assert.Contains("battery", ToolCallAudit.Redact(Args("""{"kind":"battery"}""")));
        var audio = ToolCallAudit.Redact(Args("""{"volume":35,"mute":true}"""));
        Assert.Contains("35", audio);
        Assert.Contains("true", audio);
        Assert.Contains("play_pause", ToolCallAudit.Redact(Args("""{"action":"play_pause"}""")));

        var manage = ToolCallAudit.Redact(Args("""{"window":"w3","action":"close","x":10,"y":20,"width":800,"height":600}"""));
        Assert.Contains("close", manage);
        Assert.Contains("w3", manage);
        Assert.Contains("800", manage);
    }

    [Fact]
    public void Typed_text_values_keys_and_clipboard_text_stay_redacted()
    {
        var redacted = ToolCallAudit.Redact(Args("""{"text":"my-clipboard-secret","value":"typed-secret","keys":"ctrl+v","action":"type"}"""));

        Assert.DoesNotContain("my-clipboard-secret", redacted);
        Assert.DoesNotContain("typed-secret", redacted);
        Assert.DoesNotContain("ctrl+v", redacted);
    }
}
