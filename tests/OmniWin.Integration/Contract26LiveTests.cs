using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// Contract 2.6, live (owner present over Remote Desktop, hands off): a task done once through the tools is learned, replayed,
/// trusted, run from the user's words with no step-by-step calls, stopped by a renamed button and repaired; and selected text is
/// read and replaced. The fixture's own event log is the ground truth for what really arrived. Skipped unless
/// OMNIWIN_LIVE_SESSION=1 and a connected desktop. The owner's clipboard text is put back.
/// </summary>
[Trait("Category", "RealInput")]
public sealed class Contract26LiveTests(E2ESession s) : IClassFixture<E2ESession>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RecipeOf(string text) => Regex.Match(text, @"recipe=([A-Za-z0-9_-]+)").Groups[1].Value;

    /// <summary>Empties the fixture's Name box (a test-only command) and waits until the fixture says it did.</summary>
    private static async Task ClearName(DesktopFixture f)
    {
        var before = f.Count("Name", "cleared");
        await f.CommandAsync("clear-name");
        for (var i = 0; i < 40 && f.Count("Name", "cleared") == before; i++) await Task.Delay(50, Ct);
        Assert.Equal(before + 1, f.Count("Name", "cleared"));
    }

    /// <summary>The AI's own way of doing the task: observe, then three single acts.</summary>
    private async Task DoByHand(DesktopFixture f, string name, string applyLabel = "\"Apply\"")
    {
        var obs = await s.ObserveWindow(f);
        var typed = await s.DesktopAct(obs, "\"Name\"", "type", name);
        Assert.True(typed.Status is "confirmed" or "unverified", typed.FirstLine);
        obs = await s.ObserveWindow(f);
        var toggled = await s.DesktopAct(obs, "\"Agree\"", "toggle");
        Assert.True(toggled.Status is "confirmed" or "unverified", toggled.FirstLine);
        obs = await s.ObserveWindow(f);
        var clicked = await s.DesktopAct(obs, applyLabel, "click");
        Assert.True(clicked.Status is "confirmed" or "unverified", clicked.FirstLine);
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task A_task_is_learned_replayed_trusted_run_from_words_stopped_by_a_renamed_button_and_repaired()
    {
        await using var f = await DesktopFixture.StartAsync("F01");
        var word = "fixture" + Guid.NewGuid().ToString("N")[..6];      // a wording no other test or recipe uses

        // 1. First time: the request goes to the AI, which does it by hand and reports success.
        var first = Stopwatch.StartNew();
        var asked = await s.Tool("omniwin_do", new { text = $"fill the {word} form for robin" });
        Assert.StartsWith("needs_brain reason=no_rule input_sent=false", asked.Text, StringComparison.Ordinal);
        await DoByHand(f, "robin");
        Assert.Equal(1, f.Count("Apply", "click"));

        // Without params the answer names the placeholder; with them the recipe is saved.
        var probe = await s.Tool("task_done", new { success = true, template = $"fill the {word} form for {{name}}" });
        Assert.Contains("reason=template_mismatch", probe.FirstLine, StringComparison.Ordinal);
        var placeholder = Regex.Match(probe.Text, @"Placeholders: (p\d+)").Groups[1].Value;
        Assert.NotEqual(string.Empty, placeholder);
        var done = await s.Tool("task_done", new { success = true, template = $"fill the {word} form for {{name}}", @params = new Dictionary<string, string> { [placeholder] = "name" } });
        var firstRunMs = first.Elapsed.TotalMilliseconds;
        Assert.Contains("learned=true", done.FirstLine, StringComparison.Ordinal);
        Assert.Contains("steps=3 trust=candidate", done.FirstLine, StringComparison.Ordinal);
        Assert.Contains("slot name -> desktop_act type on \"Name\"", done.Text, StringComparison.Ordinal);
        var recipe = RecipeOf(done.FirstLine);

        // 2. A candidate does not run by itself; it is offered to the AI.
        var offered = await s.Tool("omniwin_do", new { text = $"fill the {word} form for sam" });
        Assert.StartsWith("needs_brain", offered.Text, StringComparison.Ordinal);
        Assert.Contains($"recipe: {recipe}(name)", offered.Text, StringComparison.Ordinal);
        Assert.Equal(1, f.Count("Apply", "click"));

        // 3. Three clean replays through workflow_run earn trust. Each one clicks Apply exactly once.
        for (var i = 1; i <= 3; i++)
        {
            await ClearName(f);                                                   // learned in an empty box: replays start empty too
            var value = "r" + i.ToString(CultureInfo.InvariantCulture);
            var run = await s.Tool("workflow_run", new { name = recipe, @params = new { name = value } });
            Assert.True(run.Status is "confirmed" or "unverified", run.Text);
            Assert.Contains("steps=3/3", run.FirstLine, StringComparison.Ordinal);
            Assert.Equal(1 + i, f.Count("Apply", "click"));
            Assert.Equal(value, f.LastValue("Name", "text_changed"));
        }

        await s.Tool("task_done", new { success = false });   // close the task the offer opened
        Assert.Contains($"learned {recipe} trust=trusted runs=3 stops=0", (await s.Tool("workflow_list", new { })).Text, StringComparison.Ordinal);

        // 4. Now the user's words run it: one call, no step-by-step work. Twenty times, each with its own value.
        var replayMs = new List<double>();
        for (var i = 1; i <= 20; i++)
        {
            var value = "Dana" + i.ToString(CultureInfo.InvariantCulture);
            await ClearName(f);
            var watch = Stopwatch.StartNew();
            var run = await s.Tool("omniwin_do", new { text = $"Please fill the {word} form for {value}" });
            replayMs.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Contains($"routed rule=recipe.{recipe}", run.Text, StringComparison.Ordinal);
            // The F01 Apply click changes nothing visible, so OmniWin honestly says unverified; the fixture's own log is the proof.
            Assert.True(run.FirstLine.StartsWith($"workflow_run name={recipe} status=", StringComparison.Ordinal) &&
                        (run.Status is "confirmed" or "unverified") && run.InputSent == true && run.FirstLine.Contains(" steps=3/3 ", StringComparison.Ordinal), run.Text);
            Assert.Equal(4 + i, f.Count("Apply", "click"));                       // exactly one more Apply click
            Assert.Equal(value, f.LastValue("Name", "text_changed"));            // exactly the value the user wrote, nothing added
        }

        // 4b. Review 27, R1: text left in the box. The task was learned in an empty box, so it must stop with nothing typed.
        var leftOver = f.LastValue("Name", "text_changed");
        var applyBefore = f.Count("Apply", "click");
        var typedBefore = f.Count("Name", "text_changed");
        var refused = await s.Tool("omniwin_do", new { text = $"fill the {word} form for Zed" });
        Assert.StartsWith("needs_brain reason=recipe_not_ready input_sent=false", refused.Text, StringComparison.Ordinal);
        Assert.Contains("the field already has text", refused.Text, StringComparison.Ordinal);
        Assert.Equal(typedBefore, f.Count("Name", "text_changed"));
        Assert.Equal(applyBefore, f.Count("Apply", "click"));
        Assert.Equal(leftOver, f.LastValue("Name", "text_changed"));
        await s.Tool("task_done", new { success = false });                      // the AI would now deal with it; nothing to learn here

        // 5. Drift: the fixture renames its Apply button. The replay must stop there and click nothing else.
        await f.CommandAsync("rename-apply");
        for (var i = 0; i < 40 && f.Count("Apply", "renamed") == 0; i++) await Task.Delay(50, Ct);
        Assert.Equal(1, f.Count("Apply", "renamed"));
        await ClearName(f);
        var clicksBefore = f.Count(null, "click");
        var stopped = await s.Tool("omniwin_do", new { text = $"fill the {word} form for Eve" });
        Assert.Contains("stopped_at=3", stopped.FirstLine, StringComparison.Ordinal);
        Assert.Equal(clicksBefore, f.Count(null, "click"));                       // no click arrived anywhere
        Assert.Contains($"learned {recipe} trust=candidate", (await s.Tool("workflow_list", new { })).Text, StringComparison.Ordinal);

        // 6. Repair: the AI finishes from where it stopped (one click on the renamed button) and reports success.
        var obs = await s.ObserveWindow(f);
        var finish = await s.DesktopAct(obs, "\"Save changes\"", "click");
        Assert.True(finish.Status is "confirmed" or "unverified", finish.FirstLine);
        var repaired = await s.Tool("task_done", new { success = true });
        Assert.Contains($"learned=true recipe={recipe}-v2 steps=3", repaired.FirstLine, StringComparison.Ordinal);
        Assert.Contains($"repaired from step 3 of {recipe}", repaired.Text, StringComparison.Ordinal);

        // 7. The repaired version earns its own trust and then runs from the words again.
        for (var i = 1; i <= 3; i++)
        {
            await ClearName(f);
            var run = await s.Tool("workflow_run", new { name = recipe + "-v2", @params = new { name = "v" + i.ToString(CultureInfo.InvariantCulture) } });
            Assert.True(run.Status is "confirmed" or "unverified", run.Text);
        }

        await ClearName(f);
        var clicks = f.Count(null, "click");
        var again = await s.Tool("omniwin_do", new { text = $"fill the {word} form for Finn" });
        Assert.Contains($"routed rule=recipe.{recipe}-v2", again.Text, StringComparison.Ordinal);
        Assert.Equal(clicks + 1, f.Count(null, "click"));

        // Numbers for the report. The first run here has NO thinking time in it (a test drives the tools back to back), so the
        // real first run with an AI is slower than this, and the real speed-up larger.
        replayMs.Sort();
        var report = new
        {
            what = "contract 2.6 learning on the F01 fixture (3 steps: type, toggle, click), through the MCP server, real input",
            first_run_by_hand_ms = Math.Round(firstRunMs, 1),
            first_run_note = "7 tool calls driven by a test with no AI thinking between them; with an AI each call adds its thinking time",
            replay_from_words_ms = new { runs = replayMs.Count, p50 = Math.Round(replayMs[replayMs.Count / 2], 1), max = Math.Round(replayMs[^1], 1) },
            tool_calls = new { first_run = 7, replay = 1 },
            trusted_replays_from_words = 20,
            drift = "renamed button: replay stopped at step 3, no click arrived, repaired as -v2",
        };
        var dir = Path.Combine(FindRepoRoot(), "artifacts", "benchmarks");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "learning-v1.json");
        for (var v = 2; File.Exists(path); v++) path = Path.Combine(dir, $"learning-v{v}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Ct);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OmniWin.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("OmniWin.sln was not found above the test folder.");
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Selected_text_is_read_replaced_in_place_and_the_clipboard_text_is_put_back()
    {
        if (!ClipboardState.TryRead(out var original, out var why)) Assert.Skip("Not run, nothing touched: " + why + ".");
        if (original is { Length: > 1_000_000 }) Assert.Skip("Not run, nothing touched: the clipboard text is too long to put back.");
        var marker = "OmniWin-test-" + Guid.NewGuid().ToString("N");
        await using var f = await DesktopFixture.StartAsync("F01");
        try
        {
            Assert.Equal("confirmed", (await s.Tool("clipboard_set", new { text = marker })).Status);
            var obs = await s.ObserveWindow(f);
            Assert.True((await s.DesktopAct(obs, "\"Name\"", "type", "i has wrote the report")).InputSent);
            Assert.True((await s.Tool("desktop_keys", new { keys = "ctrl+a", window = f.Title })).InputSent);

            var got = await s.Tool("selection_get", new { });
            Assert.StartsWith("status=confirmed input_sent=false", got.FirstLine, StringComparison.Ordinal);
            Assert.EndsWith("i has wrote the report", got.Text, StringComparison.Ordinal);
            var sel = Regex.Match(got.Text, @"sel=([0-9a-f]{12})").Groups[1].Value;

            var replaced = await s.Tool("selection_replace", new { sel, text = "I have written the report." });

            Assert.True(replaced.Status is "confirmed" or "unverified", replaced.Text);
            Assert.True(replaced.InputSent);
            Assert.Equal("I have written the report.", f.LastValue("Name", "text_changed"));    // the app's own log
            Assert.Equal("confirmed", replaced.Status);
            Assert.Contains(marker, (await s.Tool("clipboard_get", new { })).Text, StringComparison.Ordinal);   // the earlier clipboard text is back

            // The same id cannot be used twice: the selection it named is gone.
            var stale = await s.Tool("selection_replace", new { sel, text = "again" });
            Assert.False(stale.InputSent);
            Assert.Equal("I have written the report.", f.LastValue("Name", "text_changed"));
        }
        finally
        {
            if (original is null) Assert.True(ClipboardState.Empty(), "the clipboard was empty before the test and could not be emptied again");
            else Assert.Equal("confirmed", (await s.Tool("clipboard_set", new { text = original })).Status);
        }
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task Text_insert_puts_dictated_text_at_the_caret_and_puts_the_clipboard_text_back()
    {
        if (!ClipboardState.TryRead(out var original, out var why)) Assert.Skip("Not run, nothing touched: " + why + ".");
        if (original is { Length: > 1_000_000 }) Assert.Skip("Not run, nothing touched: the clipboard text is too long to put back.");
        var marker = "OmniWin-test-" + Guid.NewGuid().ToString("N");
        await using var f = await DesktopFixture.StartAsync("F01");
        try
        {
            Assert.Equal("confirmed", (await s.Tool("clipboard_set", new { text = marker })).Status);
            var obs = await s.ObserveWindow(f);
            Assert.True((await s.DesktopAct(obs, "\"Name\"", "type", "Dear team, ")).InputSent);

            var inserted = await s.Tool("text_insert", new { text = "the report is ready." });

            Assert.True(inserted.InputSent);
            Assert.Equal("Dear team, the report is ready.", f.LastValue("Name", "text_changed"));   // the app's own log
            Assert.Equal("confirmed", inserted.Status);
            Assert.Contains(marker, (await s.Tool("clipboard_get", new { })).Text, StringComparison.Ordinal);
        }
        finally
        {
            if (original is null) Assert.True(ClipboardState.Empty(), "the clipboard was empty before the test and could not be emptied again");
            else Assert.Equal("confirmed", (await s.Tool("clipboard_set", new { text = original })).Status);
        }
    }

    [Fact(Skip = Live.Skip, SkipUnless = nameof(Live.Enabled), SkipType = typeof(Live))]
    public async Task A_password_field_is_never_read_as_a_selection_and_nothing_is_inserted_into_it()
    {
        await using var f = await DesktopFixture.StartAsync("F15");
        var obs = await s.ObserveWindow(f);
        Assert.True((await s.DesktopAct(obs, "\"WinForms secret\"", "focus")).Status is "confirmed" or "unverified");
        await s.Tool("desktop_keys", new { keys = "ctrl+a", window = f.Title });
        var events = f.Events().Count;

        var got = await s.Tool("selection_get", new { });
        var inserted = await s.Tool("text_insert", new { text = "should never arrive" });

        Assert.False(got.InputSent);
        Assert.Equal("denied", got.Status);
        Assert.DoesNotContain("fixture-secret-712", got.Text, StringComparison.Ordinal);
        Assert.False(inserted.InputSent);
        Assert.Equal("denied", inserted.Status);
        Assert.Equal(events, f.Events().Count);        // the password box logged no change
    }
}
