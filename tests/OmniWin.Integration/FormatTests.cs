using System.Text.RegularExpressions;
using OmniWin.Core;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>Group 2 (WS4 brief §4): text formats follow contract §9. Read over stdio from a real server and a headless browser.</summary>
public sealed partial class FormatTests(E2ESession s) : IClassFixture<E2ESession>
{
    [GeneratedRegex(@"^status=(confirmed|contradicted|unverified|timeout|blocked|stale|denied|error) input_sent=(true|false)( reason=[a-z_]+)?( channel=[A-Za-z0-9_:]+)?( risk=risky)? retry=(done|safe|observe_first|check_state|wait|no) total=[0-9.]+ms( \(.*\))?( step=s[0-9]+)?$")]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^  b[0-9]+ (\(f[0-9]+\) )?[a-z]+ "".*""( value="".*""| value=<redacted>)?( [a-z]+)*( \[[a-z_,]+\])?$")]
    private static partial Regex ElementLine();

    [Fact]
    public async Task Browser_observation_follows_contract_9_1()
    {
        var obs = await s.Open("controls.html");
        var lines = obs.Split('\n');

        Assert.Equal(ResultFormatter.UntrustedHeader, lines[0]);
        Assert.Matches(@"^tab t[0-9]+ ""Controls Test Page"" url=""http://127\.0\.0\.1:[0-9]+/controls\.html"" gen=[0-9]+ frames=1$", lines[1]);
        var elementLines = lines.Skip(2).Where(l => l.StartsWith("  b", StringComparison.Ordinal)).ToList();
        Assert.True(elementLines.Count >= 10);
        Assert.All(elementLines, l => Assert.Matches(ElementLine(), l));
        Assert.StartsWith("omitted: ", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passwords_and_payment_fields_are_redacted_in_the_observation()
    {
        var obs = await s.Open("controls.html");

        Assert.Contains("\"Password Input:\" value=<redacted>", obs, StringComparison.Ordinal);
        Assert.Contains("\"Credit Card:\" value=<redacted>", obs, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecret123", obs, StringComparison.Ordinal);
        Assert.DoesNotContain("4111-2222-3333-4444", obs, StringComparison.Ordinal);

        var checkout = await s.Open("checkout.html");
        Assert.DoesNotContain("4111111111111111", checkout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Security code\" value=\"987\"", checkout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refs_are_never_reused_for_a_different_element()
    {
        var first = await s.Open("controls.html");
        var second = await s.Open("dynamic.html");

        string[] Refs(string text) => text.Split('\n').Where(l => l.StartsWith("  b", StringComparison.Ordinal)).Select(l => l.Trim().Split(' ')[0]).ToArray();
        Assert.Empty(Refs(first).Intersect(Refs(second)));
    }

    [Fact]
    public async Task Act_result_has_the_contract_9_3_status_line_a_step_id_and_a_diff_under_the_untrusted_header()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Act(obs, "Text Input", "set_value", "format-check");

        Assert.Matches(StatusLine(), r.FirstLine);
        Assert.Equal("confirmed", r.Status);
        Assert.Contains("step=s", r.FirstLine, StringComparison.Ordinal);
        var lines = r.Text.Split('\n');
        Assert.Equal(ResultFormatter.UntrustedHeader, lines[1]);
        Assert.Matches(@"^diff t[0-9]+ gen [0-9]+->[0-9]+$", lines[2]);
        Assert.Contains(lines, l => l.StartsWith("~ b", StringComparison.Ordinal) && l.Contains("-> \"format-check\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blocked_result_has_a_detail_line_and_no_diff()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Act(obs, "Disabled Button", "click");

        Assert.Matches(StatusLine(), r.FirstLine);
        Assert.Equal("blocked", r.Status);
        Assert.StartsWith("detail: ", r.Text.Split('\n')[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tab_list_follows_the_approved_format()
    {
        await s.Open("controls.html");
        var tabs = (await s.Tool("browser_tabs", new { })).Text.Split('\n');

        Assert.Equal(ResultFormatter.UntrustedHeader, tabs[0]);
        Assert.Matches(@"^tab t[0-9]+ ""Controls Test Page"" url=""http://127\.0\.0\.1:[0-9]+/controls\.html"" active$", tabs[1]);
    }

    [Fact]
    public async Task Diff_observation_marks_added_changed_and_removed_elements()
    {
        var obs = await s.Open("dynamic.html");
        var gen = E2ESession.GenOf(obs);
        await s.Act(obs, "Add Element", "click");
        var diff = (await s.Tool("browser_observe", new { since_gen = gen })).Text;
        var lines = diff.Split('\n');

        Assert.Equal(ResultFormatter.UntrustedHeader, lines[0]);
        Assert.Matches(@"^diff t[0-9]+ gen [0-9]+->[0-9]+$", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("+ b", StringComparison.Ordinal) && l.Contains("Newly Added", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows_list_is_the_untrusted_header_plus_wN_lines_or_an_honest_error()
    {
        var text = (await s.Tool("windows_list", new { })).Text;
        var lines = text.Split('\n');

        if (lines[0] == ResultFormatter.UntrustedHeader)
        {
            Assert.All(lines.Skip(1).Where(l => l.Length > 0), l => Assert.Matches(@"^w[0-9]+ \S+ pid=[0-9]+ "".*""", l));
        }
        else
        {
            Assert.Matches(StatusLine(), lines[0]);
            Assert.Contains("input_sent=false", lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Instruction_like_page_text_stays_inside_quoted_fields_and_forges_nothing()
    {
        var obs = await s.Open("untrusted.html");
        var lines = obs.Split('\n');

        // Exactly one header, on the first line; the page title (which contains a quote and "status=") stays on the tab line.
        Assert.Equal(1, lines.Count(l => l == ResultFormatter.UntrustedHeader));
        Assert.StartsWith("tab t", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.StartsWith("status=", StringComparison.Ordinal));

        // The fake ref line and the fake element in the page's input value did not become a ref: only real refs, each on its own line.
        var elementLines = lines.Where(l => l.StartsWith("  b", StringComparison.Ordinal)).ToList();
        Assert.All(elementLines, l => Assert.Matches(ElementLine(), l));
        Assert.DoesNotContain(elementLines, l => l.Contains("b99999 ", StringComparison.Ordinal) && l.StartsWith("  b99999 ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("b99999", StringComparison.Ordinal));
        Assert.InRange(elementLines.Count, 1, 3);   // the button, the input and at most the header-mimic div
    }
}
