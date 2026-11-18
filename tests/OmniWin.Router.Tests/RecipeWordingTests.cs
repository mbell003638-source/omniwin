using OmniWin.Core;
using OmniWin.Server.Learning;

namespace OmniWin.Router.Tests;

/// <summary>
/// The wording matcher on a labelled set (contract 2.6 gate: no request may ever run the wrong recipe). Four trusted recipes; every
/// phrase says which recipe it must run, with which value, or that it must run nothing.
/// </summary>
public sealed class RecipeWordingTests
{
    private sealed record Case(string Text, string? Recipe, string? Value);

    private static readonly string[] Names = ["bob", "Alice", "Dr. Okafor", "jean-luc", "Mary Ann", "x2", "Team 7", "o'neil", "ZOE", "li wei", "q", "anna maria lopez"];

    private static readonly string[] Files = ["invoice 42", "Budget_2026.xlsx", "notes", "Q3-report final", "letter to sam", "a.b", "TODO", "plan v2"];

    private static IEnumerable<Case> Cases()
    {
        string[] before = ["", "please ", "Hey, ", "could you ", "ok "];
        string[] after = ["", " please", ".", " now", " thanks"];

        // Must run, with exactly this value (as the user wrote it, when it can be found unchanged in the request).
        foreach (var name in Names)
        {
            for (var i = 0; i < before.Length; i++)
            {
                yield return new Case($"{before[i]}fill the form for {name}{after[i]}", "form", name);
            }
        }

        foreach (var file in Files)
        {
            for (var i = 0; i < before.Length; i++)
            {
                yield return new Case($"{before[i]}open {file} in word{after[i]}", "word", file);
                yield return new Case($"{before[i]}export report {file} as pdf{after[i]}", "export", file);
            }
        }

        foreach (var b in before)
        {
            foreach (var a in after) yield return new Case($"{b}start my morning setup{a}", "morning", null);
        }

        // Must run nothing: near-misses, other tasks, risky words, joined requests, over-long requests, empty slots.
        string[] nothing =
        [
            "fill the form", "fill the form for", "fill form for bob", "fill the forms for bob", "fill in the form for bob", "the form for bob",
            "fill the form for bob and then close it", "fill the form for bob then email it", "fill the form for bob but skip the address",
            "fill the form for bob after lunch also print it", "fill the form for bob plus the other one",
            "fill the form for bob with the new address from the letter i got", "refill the form for bob", "fill the form for bob?!? and alice",
            "delete the form for bob", "fill the form for bob and send it", "fill the form for bob and submit", "remove bob from the form",
            "open in word", "open  in word", "open invoice 42 in excel", "open invoice 42", "open word", "open invoice 42 in word and print it",
            "open invoice 42 in word then send it to sam", "reopen invoice 42 in word", "open invoice 42 in word and excel",
            "open the file that i was working on yesterday afternoon in the office in word", "open invoice 42 in words",
            "export report as pdf", "export report q3 as docx", "export q3 as pdf", "export report q3", "export the report q3 as pdf",
            "export report q3 as pdf and send it to the team", "export report q3 as pdf and delete the old one", "report q3 as pdf",
            "start my morning", "start morning setup", "start my morning setup and open mail", "start my evening setup", "my morning setup",
            "start my morning setup then play music", "stop my morning setup", "restart my morning setup", "install my morning setup",
            "what is my morning setup", "start my morning setup for bob", "run my morning setup", "morning",
            "fill the form for a very long name that goes on and on and on", "fill the form for bob, carol, dave, erin, frank and gina",
            "pay the invoice", "send the report", "buy more paper", "format the disk", "shut down", "", "   ", "?", "fill", "open", "export",
            "tell me a joke", "summarise this page", "book a flight to paris next tuesday", "what should i do today", "write an email to bob",
            "fill the form for {name}", "open {file} in word", "fill the form for bob\nopen notes in word", "fill the form for *", "fill the form for ../..",
        ];
        foreach (var text in nothing) yield return new Case(text, null, null);
    }

    private static async Task<(LearningRig Rig, Dictionary<string, string> Names)> Build()
    {
        var rig = new LearningRig();
        rig.Screen.Add("Name", "edit");
        rig.Screen.Add("File name", "edit");
        rig.Screen.Add("Report", "edit");
        rig.Screen.Add("Agree", "checkbox");
        rig.Screen.Add("Apply");
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        async Task Learn(string key, string request, string? template, string? typeInto, string typed)
        {
            await rig.Ask(request);
            if (typeInto is not null) await rig.Do((typeInto, "type", typed), ("Agree", "toggle", null), ("Apply", "click", null));
            else await rig.Do(("Agree", "toggle", null), ("Apply", "click", null));
            Dictionary<string, string>? map = typeInto is null ? null : new() { [rig.Log.Steps.Last(s => s.ValueParam is not null).ValueParam!] = "v" };
            var answer = rig.Learning.TaskDone(true, null, template, map);
            Assert.Contains("learned=true", answer, StringComparison.Ordinal);
            names[key] = LearningRig.Token(answer, "recipe=");
            await rig.Trust(names[key]);
        }

        await Learn("form", "fill the form for bob", "fill the form for {v}", "Name", "bob");
        await Learn("word", "open invoice 42 in word", "open {v} in word", "File name", "invoice 42");
        await Learn("export", "export report q3 as pdf", "export report {v} as pdf", "Report", "q3");
        await Learn("morning", "start my morning setup", null, null, string.Empty);
        return (rig, names);
    }

    [Fact]
    public async Task No_phrase_in_the_labelled_set_runs_the_wrong_recipe_or_takes_the_wrong_value()
    {
        var (rig, names) = await Build();
        using var _ = rig;
        var cases = Cases().ToList();
        Assert.True(cases.Count >= 200, $"only {cases.Count} labelled phrases");

        var wrongRecipe = new List<string>();
        var wrongValue = new List<string>();
        var missed = new List<string>();
        foreach (var c in cases)
        {
            var match = rig.Learning.FindExact(c.Text, out var _);
            var got = match is null ? null : names.First(n => n.Value == match.Recipe.Name).Key;
            if (got != c.Recipe)
            {
                if (got is null) missed.Add(c.Text);
                else wrongRecipe.Add($"\"{c.Text}\" ran {got}, expected {c.Recipe ?? "nothing"}");
            }
            else if (match is not null && c.Value is not null && match.Values["v"] != c.Value &&
                     !string.Equals(match.Values["v"], OmniWin.Server.Router.PhraseRouter.Normalize(c.Value), StringComparison.Ordinal))
            {
                wrongValue.Add($"\"{c.Text}\" gave \"{match.Values["v"]}\", expected \"{c.Value}\"");
            }
        }

        Assert.True(wrongRecipe.Count == 0, "wrong recipe: " + string.Join("; ", wrongRecipe));
        Assert.True(wrongValue.Count == 0, "wrong value: " + string.Join("; ", wrongValue));
        // Coverage is reported, and bounded so a matcher that answers nothing cannot pass.
        var shouldRun = cases.Count(c => c.Recipe is not null);
        TestContext.Current.TestOutputHelper?.WriteLine($"labelled set: {cases.Count} phrases, {shouldRun} must run, {missed.Count} of those missed, 0 wrong recipe, 0 wrong value");
        var artifacts = Path.Combine(FindRepoRoot(), "artifacts", "benchmarks");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "recipe-wording-coverage.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            phrases = cases.Count, must_run = shouldRun, must_not_run = cases.Count - shouldRun, missed = missed.Count,
            wrong_recipe = wrongRecipe.Count, wrong_value = wrongValue.Count, missed_phrases = missed,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Assert.True(missed.Count <= shouldRun / 10, $"{missed.Count} of {shouldRun} phrases that should run did not: " + string.Join("; ", missed.Take(12)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OmniWin.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("OmniWin.sln was not found above the test folder.");
    }

    [Fact]
    public async Task Running_the_whole_labelled_set_through_omniwin_do_sends_input_only_for_phrases_that_must_run()
    {
        var (rig, _) = await Build();
        using var __ = rig;
        foreach (var c in Cases().Where(c => c.Recipe is null))
        {
            rig.Screen.Acts.Clear();
            var before = rig.System.Total;
            var text = await rig.Ask(c.Text);
            Assert.True(rig.Screen.Acts.Count == 0, $"\"{c.Text}\" sent input: {text.Split('\n')[0]}");
            Assert.True(rig.System.Total == before || text.Contains("routed rule=", StringComparison.Ordinal), c.Text);
            Assert.DoesNotContain("routed rule=recipe.", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("open {file} in word", "open {file} in word", "file")]
    [InlineData("  Please OPEN {file} in Word! ", "open {file} in word", "file")]
    [InlineData("move {a} to {b} folder", "move {a} to {b} folder", "a,b")]
    public void A_template_is_stored_in_one_canonical_form(string raw, string canonical, string slots)
    {
        var template = RecipeTemplate.Parse(raw, out var problem);

        Assert.Null(problem);
        Assert.Equal(canonical, template!.Text);
        Assert.Equal(slots.Split(','), template.Slots);
    }

    [Theory]
    [InlineData("{x}")]                       // would match anything
    [InlineData("open {x}")]                  // one plain word only
    [InlineData("open {a} {b} in word")]      // two slots side by side
    [InlineData("open {a} and {a} in word")]  // a slot twice
    [InlineData("open {File} in word")]       // slot names are lower case
    [InlineData("open {a in word")]           // broken brace
    [InlineData("")]
    public void A_template_that_breaks_a_rule_is_refused(string raw)
    {
        Assert.Null(RecipeTemplate.Parse(raw, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Two_slots_take_their_own_values_and_a_value_keeps_the_spelling_of_the_request()
    {
        var template = RecipeTemplate.Parse("move {a} to {b} folder", out _)!;

        var values = template.Match("move budget_2026.xlsx to old stuff folder", "Move Budget_2026.xlsx to Old Stuff folder")!;

        Assert.Equal("Budget_2026.xlsx", values["a"]);
        Assert.Equal("Old Stuff", values["b"]);
        Assert.Null(template.Match("move budget to folder"));
        Assert.Null(template.Match("move a to b folder now and then"));
    }

    [Theory]
    [InlineData("invoice 42 - Word", "* - Word")]
    [InlineData("Inbox - sam@example.com - Outlook", "* - Outlook")]
    [InlineData("notes.txt – Notepad", "* – Notepad")]
    [InlineData("Save As", "Save As")]
    [InlineData("Calculator", "Calculator")]
    [InlineData(" - ", " - ")]
    [InlineData("", "")]
    public void A_learned_step_keeps_the_app_part_of_a_window_title_and_lets_the_document_part_vary(string title, string pattern)
    {
        Assert.Equal(pattern, LearningService.GeneralizeTitle(title));
        Assert.True(System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, title, ignoreCase: true) || title.Length == 0);
    }

    [Fact]
    public void The_events_decide_trust_in_order()
    {
        var card = new RecipeCard(1, "r", "g", "do the thing", [], [], 2, 0, [], [], true, false, null, DateTimeOffset.UnixEpoch);
        RecipeEvent E(string kind, string? alias = null) => new(DateTimeOffset.UnixEpoch, "r", kind, Alias: alias);

        Assert.False(RecipeStore.StateOf(card, [E("replay_ok"), E("replay_ok")], 3).Trusted);
        Assert.True(RecipeStore.StateOf(card, [E("replay_ok"), E("replay_ok"), E("replay_ok")], 3).Trusted);
        Assert.False(RecipeStore.StateOf(card, [E("replay_ok"), E("replay_ok"), E("replay_ok"), E("replay_stopped")], 3).Trusted);
        Assert.False(RecipeStore.StateOf(card, [E("replay_ok"), E("replay_ok"), E("replay_ok"), E("wrong_pick"), E("replay_ok")], 3).Trusted);
        var disabled = RecipeStore.StateOf(card, [E("replay_ok"), E("replay_ok"), E("replay_ok"), E("disabled")], 3);
        Assert.True(disabled.Trusted);
        Assert.False(disabled.Active);
        Assert.Equal(["do the thing", "do it {x}"], RecipeStore.StateOf(card, [E("alias_added", "do it {x}"), E("alias_added", "do it {x}")], 3).Templates);
        Assert.Equal(ActStatus.Confirmed, ActStatus.Confirmed);
    }
}
