using System.Security.Cryptography;
using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Learning;

namespace OmniWin.Router.Tests;

/// <summary>
/// Contract 2.6 against a pretend screen: what is learned, when a recipe is trusted, what runs without the AI and with which values,
/// what is refused, what a drifted screen does, and that nothing on disk is ever replaced or deleted.
/// </summary>
public sealed class LearningTests
{
    private static Dictionary<string, string> Hashes(params string[] dirs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                map[file] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }
        }

        return map;
    }

    // ------------------------------------------------------------------ learning

    [Fact]
    public async Task A_task_the_AI_did_is_saved_as_a_candidate_recipe_and_does_not_run_by_itself_yet()
    {
        using var rig = new LearningRig();
        rig.Screen.Add("Name", "edit");
        rig.Screen.Add("Agree", "checkbox");
        rig.Screen.Add("Apply");

        var asked = await rig.Ask("fill the form for bob");
        Assert.StartsWith("needs_brain reason=no_rule input_sent=false", asked, StringComparison.Ordinal);
        Assert.Contains("learn: when the task is done", asked, StringComparison.Ordinal);

        await rig.Do(("Name", "type", "bob"), ("Agree", "toggle", null), ("Apply", "click", null));
        var answer = rig.Learning.TaskDone(true, null, "Fill the form for {name}", new Dictionary<string, string> { ["p1"] = "name" });

        Assert.StartsWith("task_done status=confirmed input_sent=false retry=done learned=true recipe=fill-the-form-for-bob steps=3 trust=candidate", answer, StringComparison.Ordinal);
        Assert.Contains("template: fill the form for {name}", answer, StringComparison.Ordinal);
        Assert.Contains("slot name -> desktop_act type on \"Name\"", answer, StringComparison.Ordinal);
        var state = rig.State("fill-the-form-for-bob");
        Assert.False(state.Trusted);
        Assert.Equal(["name"], state.Card.Params);
        Assert.True(state.Card.AutoRunEligible);

        rig.Screen.Acts.Clear();
        var again = await rig.Ask("fill the form for alice");
        Assert.StartsWith("needs_brain", again, StringComparison.Ordinal);                      // a candidate never runs by itself
        Assert.Contains("recipe: fill-the-form-for-bob(name) steps=3 trust=candidate", again, StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);
    }

    [Fact]
    public async Task After_three_clean_replays_the_same_request_with_a_new_value_runs_without_the_AI_and_keeps_the_value_as_written()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        await rig.Trust(name);
        rig.Screen.Acts.Clear();

        var text = await rig.Ask("Please fill the form for Alice-Marie.");

        Assert.StartsWith($"workflow_run name={name} status=confirmed input_sent=true", text, StringComparison.Ordinal);
        Assert.Contains($"routed rule=recipe.{name}", text, StringComparison.Ordinal);
        Assert.Equal([("Name", "type", "Alice-Marie"), ("Agree", "toggle", null), ("Apply", "click", null)], rig.Screen.Acts);
    }

    [Fact]
    public async Task One_stopped_replay_takes_the_trust_away_again()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        await rig.Trust(name);

        rig.Screen.OnScreen.Remove("Apply");
        var run = await rig.Replay(name, new() { ["name"] = "x" });

        Assert.Equal(3, run.StoppedAt);
        Assert.False(rig.State(name).Trusted);
        Assert.Equal(1, rig.State(name).Stops);
    }

    [Fact]
    public async Task A_task_with_a_failed_or_unrecorded_act_is_saved_as_noisy()
    {
        using var rig = new LearningRig();
        rig.Screen.Add("Name", "edit");
        rig.Screen.Add("Help");
        rig.Screen.Add("Apply");
        rig.Screen.Silent.Add("Help");

        await rig.Ask("fill the short form for bob");
        await rig.Do(("Name", "type", "bob"), ("Help", "click", null), ("Apply", "click", null));
        var answer = rig.Learning.TaskDone(true, null, null, null);

        Assert.Contains("learned=true", answer, StringComparison.Ordinal);
        Assert.Contains("steps=2", answer, StringComparison.Ordinal);                             // the silent click is not in the recipe
        Assert.Contains("note: an act failed, was not recorded", answer, StringComparison.Ordinal);
        var name = LearningRig.Token(answer, "recipe=");
        Assert.True(rig.State(name).Card.Noisy);
        Assert.False(rig.State(name).Card.AutoRunEligible);

        // However often it replays cleanly, a recipe that may be missing a step never runs without the AI (review 27, L2).
        await rig.Trust(name);
        rig.Screen.Acts.Clear();
        Assert.StartsWith("needs_brain", await rig.Ask("fill the short form for bob"), StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);
    }

    [Fact]
    public async Task An_act_by_a_tool_that_cannot_be_replayed_makes_the_task_noisy()
    {
        using var rig = new LearningRig();
        rig.Screen.Add("Name", "edit");
        rig.Screen.Add("Apply");
        await rig.Ask("fill the engine form for bob");
        await rig.Do(("Name", "type", "bob"), ("Apply", "click", null));
        rig.Learning.NoteUntrackedAct();                       // what the server's filter reports for engine_call, selection_replace ...

        var answer = rig.Learning.TaskDone(true, null, null, null);

        Assert.Contains("learned=true", answer, StringComparison.Ordinal);
        Assert.Contains("auto_run=no", answer, StringComparison.Ordinal);
        Assert.Contains("never runs by itself", answer, StringComparison.Ordinal);
        Assert.False(rig.State(LearningRig.Token(answer, "recipe=")).Card.AutoRunEligible);
    }

    [Fact]
    public void The_filter_counts_only_results_that_say_input_was_sent_and_not_a_recipe_omniwin_do_ran_itself()
    {
        static ModelContextProtocol.Protocol.CallToolResult Result(string text) =>
            new() { Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = text }] };

        Assert.True(OmniWin.Server.Hosting.RunLogFilter.SentInput(Result("status=unverified input_sent=true retry=check_state total=4ms\nengine=agent-desktop")));
        Assert.True(OmniWin.Server.Hosting.RunLogFilter.SentInput(Result("status=confirmed input_sent=true channel=api retry=done total=1ms\nrouted rule=volume.set")));
        Assert.False(OmniWin.Server.Hosting.RunLogFilter.SentInput(Result("status=confirmed input_sent=false retry=done total=1ms")));
        Assert.False(OmniWin.Server.Hosting.RunLogFilter.SentInput(Result("needs_brain reason=no_rule input_sent=false retry=safe")));
        Assert.False(OmniWin.Server.Hosting.RunLogFilter.SentInput(Result("workflow_run name=x status=stale input_sent=true steps=2/3 stopped_at=3 total=9ms\nrouted rule=recipe.x")));
    }

    // ------------------------------------------------------------------ refusals

    [Theory]
    [InlineData("fill the form for bob", null, null, 1, "too_few_steps")]
    [InlineData("delete the old form for bob", null, null, 3, "risky_words")]
    [InlineData("fill the form for bob", "empty the form for {name}", "name", 3, "template_mismatch")]   // does not match the request
    [InlineData("fill the form for bob", "fill the form for {name} {extra}", "name", 3, "template_mismatch")]   // two slots side by side
    [InlineData("fill the form for bob", "{name}", "name", 3, "template_mismatch")]               // would match anything
    [InlineData("fill the form for bob", "fill the form for {who}", "name", 3, "template_mismatch")]   // slot without a placeholder
    [InlineData("volume 30", null, null, 3, "template_conflict")]                                // already a built-in command
    public async Task Task_done_refuses_what_must_not_be_learned(string request, string? template, string? slot, int acts, string reason)
    {
        using var rig = new LearningRig();
        rig.Screen.Add("Name", "edit");
        rig.Screen.Add("Agree", "checkbox");
        rig.Screen.Add("Apply");
        if (request == "volume 30") rig.Learning.OpenEpisode(request);
        else await rig.Ask(request);
        (string, string, string?)[] all = [("Name", "type", "bob"), ("Agree", "toggle", null), ("Apply", "click", null)];
        await rig.Do(all[..acts]);

        var answer = rig.Learning.TaskDone(true, null, template, slot is null ? null : new Dictionary<string, string> { ["p1"] = slot });

        Assert.StartsWith($"task_done status=blocked reason={reason} input_sent=false retry=no learned=false", answer, StringComparison.Ordinal);
        Assert.False(Directory.Exists(rig.RecipesDir) && Directory.EnumerateFiles(rig.RecipesDir, "*.recipe.json").Any());
        Assert.False(Directory.Exists(rig.WorkflowsDir) && Directory.EnumerateFiles(rig.WorkflowsDir).Any());
    }

    [Fact]
    public async Task A_step_the_safety_gate_forbids_is_never_saved()
    {
        using var rig = new LearningRig();
        rig.Screen.Add("Name", "edit");
        rig.Screen.Add("Wipe");
        await rig.Ask("clear the form for bob");
        await rig.Do(("Name", "type", "bob"), ("Wipe", "click", null));
        rig.Gate.Forbidden.Add("Wipe");

        var answer = rig.Learning.TaskDone(true, null, null, null);

        Assert.StartsWith("task_done status=denied reason=policy_forbidden input_sent=false", answer, StringComparison.Ordinal);
        Assert.False(Directory.Exists(rig.WorkflowsDir) && Directory.EnumerateFiles(rig.WorkflowsDir).Any());
    }

    [Fact]
    public void Task_done_without_an_open_task_or_a_goal_and_with_learning_off_is_refused()
    {
        using var rig = new LearningRig();
        Assert.Contains("reason=no_episode", rig.Learning.TaskDone(true, null, null, null), StringComparison.Ordinal);

        using var off = new LearningRig(new OmniWinConfig { Learning = new LearningConfig { Enabled = false } });
        Assert.Contains("reason=learning_off", off.Learning.TaskDone(true, "fill the form", null, null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_false_learns_nothing_and_closes_the_task()
    {
        using var rig = new LearningRig();
        rig.Screen.Add("Name", "edit");
        rig.Screen.Add("Apply");
        await rig.Ask("fill the form for bob");
        await rig.Do(("Name", "type", "bob"), ("Apply", "click", null));

        Assert.Contains("learned=false", rig.Learning.TaskDone(false, null, null, null), StringComparison.Ordinal);
        Assert.Contains("reason=no_episode", rig.Learning.TaskDone(true, null, null, null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_learning_off_omniwin_do_neither_opens_a_task_nor_runs_a_recipe()
    {
        var folder = string.Empty;
        using (var on = new LearningRig())
        {
            var name = await on.LearnForm();
            await on.Trust(name);
            folder = on.RecipesDir;
            var config = new OmniWinConfig { Learning = new LearningConfig { AutoRun = false } };
            Assert.Null(new LearningService(on.Workflows, on.Log, on.Recipes, config).FindExact("fill the form for carol", out _));
        }

        using var off = new LearningRig(new OmniWinConfig { Learning = new LearningConfig { Enabled = false } });
        var text = await off.Ask("fill the form for carol");
        Assert.DoesNotContain("learn:", text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
    }

    // ------------------------------------------------------------------ eligibility

    [Fact]
    public async Task A_recipe_with_a_risky_step_never_runs_by_itself_however_often_it_replayed_cleanly()
    {
        using var rig = new LearningRig();
        rig.Gate.Risky.Add("Apply");
        var name = await rig.LearnForm();
        await rig.Trust(name);
        rig.Screen.Acts.Clear();

        Assert.False(rig.State(name).Card.AutoRunEligible);
        Assert.StartsWith("needs_brain", await rig.Ask("fill the form for alice"), StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);
    }

    [Fact]
    public async Task A_recipe_whose_first_control_is_not_on_screen_sends_nothing_and_hands_the_request_to_the_AI()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        await rig.Trust(name);
        rig.Screen.Acts.Clear();
        rig.Screen.OnScreen.Remove("Name");
        var before = rig.State(name);

        var text = await rig.Ask("fill the form for alice");

        Assert.StartsWith("needs_brain reason=recipe_not_ready input_sent=false", text, StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);
        Assert.Equal(before.Runs, rig.State(name).Runs);       // not counted against the recipe: the app was simply not there
        Assert.True(rig.State(name).Trusted);
    }

    // ------------------------------------------------------------------ drift and repair

    [Fact]
    public async Task A_renamed_button_stops_the_replay_at_that_step_and_the_next_task_done_saves_a_repaired_version()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        await rig.Trust(name);
        var before = Hashes(rig.WorkflowsDir, rig.RecipesDir);
        rig.Screen.Acts.Clear();

        // The app changed: "Apply" is now "Save changes".
        rig.Screen.OnScreen.Remove("Apply");
        rig.Screen.Add("Save changes");
        var stopped = await rig.Ask("fill the form for alice");

        Assert.Contains("stopped_at=3", stopped.Split('\n')[0], StringComparison.Ordinal);
        Assert.Equal([("Name", "type", "alice"), ("Agree", "toggle", null)], rig.Screen.Acts);       // nothing else was clicked
        Assert.False(rig.State(name).Trusted);

        // The AI finishes from where it stopped and reports success.
        await rig.Do(("Save changes", "click", null));
        var answer = rig.Learning.TaskDone(true, null, null, null);

        Assert.Contains($"learned=true recipe={name}-v2 steps=3", answer, StringComparison.Ordinal);
        Assert.Contains($"repaired from step 3 of {name}", answer, StringComparison.Ordinal);
        Assert.True(rig.State(name).Superseded);
        Assert.Equal(name, rig.State(name + "-v2").Card.Supersedes);
        Assert.Equal("fill the form for {name}", rig.State(name + "-v2").Card.Template);

        // The old workflow and card are byte for byte what they were.
        var after = Hashes(rig.WorkflowsDir, rig.RecipesDir);
        foreach (var (file, hash) in before.Where(f => !f.Key.EndsWith("events.jsonl", StringComparison.Ordinal))) Assert.Equal(hash, after[file]);

        // The repaired version earns its own trust and then runs with the new button.
        await rig.Trust(name + "-v2");
        rig.Screen.Acts.Clear();
        var text = await rig.Ask("fill the form for carol");
        Assert.Contains($"routed rule=recipe.{name}-v2", text, StringComparison.Ordinal);
        Assert.Equal([("Name", "type", "carol"), ("Agree", "toggle", null), ("Save changes", "click", null)], rig.Screen.Acts);
    }

    [Fact]
    public async Task Learning_the_same_wording_again_replaces_the_older_recipe_so_a_match_is_never_ambiguous()
    {
        using var rig = new LearningRig();
        var first = await rig.LearnForm("bob");
        var second = await rig.LearnForm("carol");

        Assert.NotEqual(first, second);
        Assert.True(rig.State(first).Superseded);
        await rig.Trust(second);
        Assert.Equal(second, rig.Learning.FindExact("fill the form for dave", out var ambiguous)!.Recipe.Name);
        Assert.False(ambiguous);
    }

    // ------------------------------------------------------------------ wording learned from use

    [Fact]
    public async Task A_recipe_the_AI_ran_for_a_new_wording_learns_that_wording_and_task_done_learns_nothing_new()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        await rig.Trust(name);

        var asked = await rig.Ask("do the usual form thing for carol");
        Assert.Contains($"recipe: {name}(name)", asked, StringComparison.Ordinal);
        var run = await rig.Replay(name, new() { ["name"] = "carol" });
        Assert.Equal(ActStatus.Confirmed, run.Status);
        var done = rig.Learning.TaskDone(true, null, null, null);

        Assert.Contains($"learned=false recipe={name}", done, StringComparison.Ordinal);
        Assert.Contains("do the usual form thing for {name}", rig.State(name).Templates);

        rig.Screen.Acts.Clear();
        var text = await rig.Ask("do the usual form thing for Dave");
        Assert.Contains($"routed rule=recipe.{name}", text, StringComparison.Ordinal);
        Assert.Equal(("Name", "type", "Dave"), rig.Screen.Acts[0]);
    }

    [Fact]
    public async Task A_wording_is_learned_only_on_task_done_success_and_only_when_the_recipe_was_the_whole_task()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        rig.Screen.Add("Print");
        string[] only = ["fill the form for {name}"];

        // No task_done at all: nothing is learned (owner decision 2).
        await rig.Ask("get the paperwork ready for carol");
        await rig.Replay(name, new() { ["name"] = "carol" });
        Assert.Equal(only, rig.State(name).Templates);

        // task_done(success=false): nothing is learned.
        Assert.Contains("learned=false", rig.Learning.TaskDone(false, null, null, null), StringComparison.Ordinal);
        Assert.Equal(only, rig.State(name).Templates);

        // The recipe was only part of the task (the AI also pressed Print): the request means more than the recipe does.
        await rig.Ask("fill the form for carol then press print");
        await rig.Replay(name, new() { ["name"] = "carol" });
        await rig.Do(("Print", "click", null));
        var partial = rig.Learning.TaskDone(true, null, null, null);
        Assert.Contains("learned=false", partial, StringComparison.Ordinal);
        Assert.Contains("only part of this task", partial, StringComparison.Ordinal);
        Assert.Equal(only, rig.State(name).Templates);

        // Steps before the replay count the same way.
        await rig.Ask("print and prepare the form for dora");
        await rig.Do(("Print", "click", null));
        await rig.Replay(name, new() { ["name"] = "dora" });
        Assert.Contains("only part of this task", rig.Learning.TaskDone(true, null, null, null), StringComparison.Ordinal);
        Assert.Equal(only, rig.State(name).Templates);
        Assert.Single(Directory.EnumerateFiles(rig.RecipesDir, "*.recipe.json"));
    }

    [Fact]
    public async Task A_recipe_takes_at_most_ten_extra_wordings()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        for (var i = 0; i < 13; i++)
        {
            await rig.Ask($"variant number {(char)('a' + i)} of the form for carol");
            await rig.Replay(name, new() { ["name"] = "carol" });
            rig.Learning.TaskDone(true, null, null, null);
        }

        Assert.Equal(1 + RecipeStore.MaxAliases, rig.State(name).Templates.Count);
    }

    [Fact]
    public async Task A_wording_is_not_learned_when_the_value_is_not_in_the_request_or_the_request_has_a_risky_word()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();

        await rig.Ask("do the form for my manager");
        await rig.Replay(name, new() { ["name"] = "carol" });                 // "carol" is not in the request
        rig.Learning.TaskDone(true, null, null, null);
        await rig.Ask("do the form for carol and send it");
        await rig.Replay(name, new() { ["name"] = "carol" });                 // risky word
        rig.Learning.TaskDone(true, null, null, null);

        Assert.Equal(["fill the form for {name}"], rig.State(name).Templates);
    }

    // ------------------------------------------------------------------ workflow_manage

    [Fact]
    public async Task Disable_enable_right_and_wrong_pick_are_notes_that_change_what_runs_and_delete_nothing()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        await rig.Trust(name);
        var files = Hashes(rig.WorkflowsDir, rig.RecipesDir).Keys.Order().ToList();

        Assert.Contains("trust=disabled", rig.Learning.Manage(name, "disable"), StringComparison.Ordinal);
        Assert.Null(rig.Learning.FindExact("fill the form for alice", out _));
        Assert.DoesNotContain("recipe:", await rig.Ask("fill the form for alice"), StringComparison.Ordinal);

        Assert.Contains("trust=trusted", rig.Learning.Manage(name, "enable"), StringComparison.Ordinal);
        Assert.NotNull(rig.Learning.FindExact("fill the form for alice", out _));

        Assert.Contains("trust=candidate", rig.Learning.Manage(name, "wrong_pick"), StringComparison.Ordinal);
        Assert.Null(rig.Learning.FindExact("fill the form for alice", out _));
        rig.Learning.Manage(name, "right");
        rig.Learning.Manage(name, "right");
        Assert.Contains("trust=trusted", rig.Learning.Manage(name, "right"), StringComparison.Ordinal);

        Assert.Contains("reason=not_found", rig.Learning.Manage("no-such-recipe", "disable"), StringComparison.Ordinal);
        Assert.Contains("reason=unsupported_action", rig.Learning.Manage(name, "delete"), StringComparison.Ordinal);
        Assert.Equal(files, Hashes(rig.WorkflowsDir, rig.RecipesDir).Keys.Order().ToList());
    }

    [Fact]
    public async Task A_note_that_could_not_be_written_is_an_error_and_never_reported_as_recorded()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        await rig.Trust(name);
        var events = Path.Combine(rig.RecipesDir, "events.jsonl");
        var before = File.ReadAllBytes(events);

        string answer;
        using (new FileStream(events, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // another process holds the file
        {
            answer = rig.Learning.Manage(name, "wrong_pick");
        }

        Assert.StartsWith($"workflow_manage name={name} status=error", answer, StringComparison.Ordinal);
        Assert.Contains("could NOT be written", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Recorded", answer, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(events));
        Assert.True(rig.State(name).Trusted);                                  // and the answer says so: nothing changed

        Assert.Contains("status=confirmed", rig.Learning.Manage(name, "wrong_pick"), StringComparison.Ordinal);   // free again: it works
        Assert.False(rig.State(name).Trusted);
    }

    // ------------------------------------------------------------------ nothing replaced, nothing private stored

    [Fact]
    public async Task Cards_and_workflows_never_change_after_they_are_written_and_the_events_file_only_grows()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        var before = Hashes(rig.WorkflowsDir, rig.RecipesDir);
        var events = File.ReadAllText(Path.Combine(rig.RecipesDir, "events.jsonl"));

        await rig.Trust(name);
        await rig.Ask("fill the form for zed");
        rig.Learning.Manage(name, "disable");
        rig.Learning.Manage(name, "enable");

        var after = Hashes(rig.WorkflowsDir, rig.RecipesDir);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (file, hash) in before.Where(f => !f.Key.EndsWith("events.jsonl", StringComparison.Ordinal))) Assert.Equal(hash, after[file]);
        Assert.StartsWith(events, File.ReadAllText(Path.Combine(rig.RecipesDir, "events.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_text_is_in_no_file_and_the_events_file_holds_no_request_wording()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm("zebediah");
        await rig.Trust(name, "quillfeather");
        await rig.Ask("fill the form for Hortensia");

        var events = File.ReadAllText(Path.Combine(rig.RecipesDir, "events.jsonl"));
        Assert.DoesNotContain("quillfeather", events, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hortensia", events, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fill the form", events, StringComparison.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(rig.WorkflowsDir).Concat(Directory.EnumerateFiles(rig.RecipesDir)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("quillfeather", text, StringComparison.OrdinalIgnoreCase);       // a replay value
            Assert.DoesNotContain("hortensia", text, StringComparison.OrdinalIgnoreCase);          // an instant-run value
        }
    }

    [Fact]
    public async Task Workflow_list_lines_show_trust_runs_and_both_timings()
    {
        using var rig = new LearningRig();
        var name = await rig.LearnForm();
        await rig.Trust(name);

        var line = Assert.Single(rig.Learning.ListLines());

        Assert.StartsWith($"learned {name} trust=trusted runs=3 stops=0 last=replay_ok", line, StringComparison.Ordinal);
        Assert.Contains("wording=\"fill the form for {name}\"", line, StringComparison.Ordinal);
        Assert.Contains("3 recipes".Replace("3", "1"), rig.Learning.Detail!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ config

    [Fact]
    public void Learning_and_decision_settings_are_validated()
    {
        Assert.Empty(new OmniWinConfig().Validate());
        Assert.Contains(new OmniWinConfig { Learning = new LearningConfig { PromoteAfter = 0 } }.Validate(), e => e.StartsWith("learning.", StringComparison.Ordinal));
        Assert.Contains(new OmniWinConfig { Decision = new DecisionConfig { Mode = "always" } }.Validate(), e => e.StartsWith("decision.mode", StringComparison.Ordinal));
        Assert.Contains(new OmniWinConfig { Decision = new DecisionConfig { Mode = "act" } }.Validate(), e => e.Contains("decision.provider", StringComparison.Ordinal));
        Assert.Contains(new OmniWinConfig { Decision = new DecisionConfig { Threshold = 1.5 } }.Validate(), e => e.StartsWith("decision.threshold", StringComparison.Ordinal));
    }
}
