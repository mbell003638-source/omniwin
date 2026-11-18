using OmniWin.Core;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// Group 4 (WS4 brief §4): adversarial cases, one test each, asserting the exact (status, reason, input_sent) and the
/// page's own event log. Cases that need real input or a visible window are in <see cref="LiveSessionTests"/>.
/// Group 6 (no repeated input) is covered by the contradicted / timeout / unverified cases: the page sees one input.
/// </summary>
public sealed class AdversarialTests(E2ESession s) : IClassFixture<E2ESession>
{
    private async Task<IReadOnlyList<TraceEvent>> InputEventsAsync(string page) =>
        (await s.Web.SettledAsync(page)).Where(e => e.Type is "click" or "dblclick" or "input" or "change" or "keydown" or "submit").ToList();

    [Theory]
    [InlineData("Delete account")]
    [InlineData("Delete profile")]
    [InlineData("Remove item")]
    public async Task Delete_button_link_and_form_are_denied_and_the_page_sees_nothing(string name)
    {
        var obs = await s.Open("delete.html");
        var r = await s.Act(obs, name, "click");

        Assert.Equal("denied", r.Status);
        Assert.Equal("policy_forbidden", r.Reason);
        Assert.False(r.InputSent);
        Assert.Empty(await InputEventsAsync("delete.html"));
    }

    [Theory]
    [InlineData("\"Delete\"")]
    [InlineData("\"Delete all\"")]
    [InlineData("\"DELETE\"")]
    public async Task Delete_in_any_markup_and_case_is_denied(string quotedName)
    {
        var obs = await s.Open("lexicon.html");
        var r = await s.Act(obs, quotedName, "click");

        Assert.Equal("denied", r.Status);
        Assert.Equal("policy_forbidden", r.Reason);
        Assert.False(r.InputSent);
        Assert.Empty(await InputEventsAsync("lexicon.html"));
    }

    [Theory]
    [InlineData("\"Send\"", "b-send")]
    [InlineData("\"Pay\"", "b-pay")]
    [InlineData("\"Sender\"", "b-sender")]
    [InlineData("\"Undelete\"", "b-un")]
    [InlineData("\"Payroll\"", "b-payroll")]
    public async Task Near_misses_and_risky_words_are_not_blocked_and_get_exactly_one_click(string quotedName, string id)
    {
        var obs = await s.Open("lexicon.html");
        var r = await s.Act(obs, quotedName, "click");

        Assert.NotEqual("denied", r.Status);
        Assert.True(r.InputSent);
        var clicks = (await InputEventsAsync("lexicon.html")).Where(e => e.Type == "click").ToList();
        Assert.Single(clicks);
        Assert.Equal(id, clicks[0].Id);
    }

    [Fact]
    public async Task Page_that_spoofs_the_indexer_cannot_hide_the_delete_button()
    {
        var obs = await s.Open("spoof.html");
        Assert.Contains("Delete account", obs, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Save\"", obs, StringComparison.Ordinal);

        var r = await s.Act(obs, "Delete account", "click");

        Assert.Equal("denied", r.Status);
        Assert.Equal("policy_forbidden", r.Reason);
        Assert.False(r.InputSent);
        Assert.Empty(await InputEventsAsync("spoof.html"));
    }

    [Fact]
    public async Task Element_swapped_for_a_delete_button_after_observe_is_never_clicked()
    {
        var obs = await s.Open("moving-auto.html");   // the page swaps the buttons by itself 700 ms after load
        await Task.Delay(1200, TestContext.Current.CancellationToken);

        var r = await s.Act(obs, "Click me", "click");

        Assert.Contains(r.Status, new[] { "stale", "blocked" });
        Assert.False(r.InputSent);
        Assert.Empty(await InputEventsAsync("moving-auto.html"));
    }

    [Fact]
    public async Task Button_covered_by_a_banner_in_the_same_page_is_blocked_and_nothing_is_clicked()
    {
        var obs = await s.Open("dialogs.html");
        var r = await s.Act(obs, "Covered Target", "click");

        Assert.Equal("blocked", r.Status);
        Assert.Equal("occluded_same_window", r.Reason);
        Assert.False(r.InputSent);
        Assert.DoesNotContain(await InputEventsAsync("dialogs.html"), e => e.Type == "click");
    }

    [Fact]
    public async Task Disabled_button_is_blocked_with_no_input()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Act(obs, "Disabled Button", "click");

        Assert.Equal("blocked", r.Status);
        Assert.Equal("disabled", r.Reason);
        Assert.False(r.InputSent);
        Assert.Empty(await InputEventsAsync("controls.html"));
    }

    [Fact]
    public async Task Input_that_rewrites_what_is_typed_is_contradicted_never_confirmed_and_gets_one_change()
    {
        var obs = await s.Open("rewrite.html");
        var r = await s.Act(obs, "Rewriting box", "set_value", "hello world");

        Assert.Equal("contradicted", r.Status);
        Assert.Equal("readback_mismatch", r.Reason);
        Assert.True(r.InputSent);
        Assert.Single(await InputEventsAsync("rewrite.html"), e => e.Type == "change");   // no automatic retry
    }

    [Fact]
    public async Task Button_with_no_visible_reaction_is_unverified_and_gets_exactly_one_click()
    {
        var obs = await s.Open("reaction.html");
        var r = await s.Act(obs, "Do nothing", "click");

        Assert.Equal("unverified", r.Status);
        Assert.True(r.InputSent);
        // Finding 5: the "no visible change" detail the engine promises never appears (every scan advances the generation).
        Assert.Single(await InputEventsAsync("reaction.html"), e => e.Type == "click");
    }

    [Fact]
    public async Task Act_on_a_generation_that_was_never_observed_is_stale_with_no_input()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Tool("browser_act", new { @ref = E2ESession.RefOf(obs, "Click Me"), gen = 987654, action = "click" });

        Assert.Equal("stale", r.Status);
        Assert.Equal("not_found", r.Reason);
        Assert.False(r.InputSent);
        Assert.Empty(await InputEventsAsync("controls.html"));
    }

    [Fact]
    public async Task Unknown_action_is_refused_with_no_input()
    {
        var obs = await s.Open("controls.html");
        var r = await s.Act(obs, "Click Me", "frobnicate");

        Assert.Equal("unsupported_action", r.Reason);
        Assert.False(r.InputSent);
        Assert.Empty(await InputEventsAsync("controls.html"));
    }

    [Fact]
    public async Task Cross_origin_frame_is_listed_but_nothing_inside_it_can_be_named_or_clicked()
    {
        await s.Open("frames-cross.html");
        await Task.Delay(600, TestContext.Current.CancellationToken);
        var obs = await s.Observe();

        Assert.Contains("frames=2", obs, StringComparison.Ordinal);
        Assert.Contains("cross_origin", obs, StringComparison.Ordinal);
        Assert.DoesNotContain("Inner button", obs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepting_a_delete_confirmation_dialog_is_denied_and_dismissing_it_is_allowed()
    {
        var obs = await s.Open("js-dialog.html");
        var click = await s.Act(obs, "Ask confirm", "click");
        Assert.True(click.InputSent);

        var open = await s.Observe();
        Assert.StartsWith(ResultFormatter.UntrustedHeader, open, StringComparison.Ordinal);
        Assert.Contains("dialog \"Delete everything?\" modal", open, StringComparison.Ordinal);

        var blocked = await s.Act(obs, "Plain button", "click");
        Assert.Equal("blocked", blocked.Status);
        Assert.Equal("modal_blocking", blocked.Reason);
        Assert.False(blocked.InputSent);

        var accept = await s.Tool("browser_nav", new { action = "dialog_accept" });
        Assert.Equal("denied", accept.Status);
        Assert.Equal("policy_forbidden", accept.Reason);
        Assert.False(accept.InputSent);

        var dismiss = await s.Tool("browser_nav", new { action = "dialog_dismiss" });
        Assert.True(dismiss.InputSent);
        // The page's confirm() returned false: the dismissal reached it, the acceptance did not.
        var after = await s.Observe();
        Assert.DoesNotContain("modal", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_with_a_password_is_never_shown_and_the_enter_key_in_it_is_labelled_risky_but_not_blocked()
    {
        var obs = await s.Open("login.html");
        Assert.DoesNotContain("OMNIWIN_PASSWORD_SECRET", obs, StringComparison.Ordinal);
        Assert.Contains("\"Password\" value=<redacted>", obs, StringComparison.Ordinal);

        var r = await s.Act(obs, "\"Password\"", "press", "Enter");

        Assert.True(r.InputSent);
        Assert.Equal("risky", r.Tokens.GetValueOrDefault("risk"));
        Assert.NotEqual("denied", r.Status);
        Assert.Contains(await InputEventsAsync("login.html"), e => e.Type == "submit" && e.Id == "login");
    }

    [Fact]
    public async Task File_input_and_mailto_and_download_links_are_listed_and_not_followed()
    {
        var obs = await s.Open("misc.html");

        Assert.Contains("Upload file", obs, StringComparison.Ordinal);
        Assert.Contains("link \"Mail link\"", obs, StringComparison.Ordinal);
        Assert.Contains("link \"Download link\"", obs, StringComparison.Ordinal);
        Assert.Empty(await InputEventsAsync("misc.html"));   // listing them sent nothing
    }
}
