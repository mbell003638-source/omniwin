using OmniWin.Core;
using OmniWin.Workflows;

namespace OmniWin.Router.Tests;

/// <summary>Review 27, R1: a learned type step never types into text that the first run did not have in front of it.</summary>
public sealed class FieldEmptyTests
{
    private static async Task<(LearningRig Rig, string Name)> Learn(string startText)
    {
        var rig = new LearningRig();
        rig.Screen.ProbeFields = true;
        rig.Screen.Add("Name", "edit");
        rig.Screen.Add("Agree", "checkbox");
        rig.Screen.Add("Apply");
        rig.Screen.Values["Name"] = startText;
        await rig.Ask("fill the form for bob");
        await rig.Do(("Name", "type", "bob"), ("Agree", "toggle", null), ("Apply", "click", null));
        var answer = rig.Learning.TaskDone(true, null, "fill the form for {name}", new Dictionary<string, string> { ["p1"] = "name" });
        Assert.Contains("learned=true", answer, StringComparison.Ordinal);
        rig.Screen.Acts.Clear();
        return (rig, LearningRig.Token(answer, "recipe="));
    }

    [Fact]
    public async Task Learned_in_an_empty_field_and_the_field_has_text_now_stops_at_that_step_with_nothing_typed()
    {
        var (rig, name) = await Learn(string.Empty);
        using var _ = rig;
        rig.Screen.Values["Name"] = "the owner's own notes";

        var run = await rig.Replay(name, new() { ["name"] = "alice" });

        Assert.Equal(1, run.StoppedAt);
        Assert.Equal(ActStatus.Stale, run.Status);
        Assert.Equal(ReasonCodes.FieldNotEmpty, run.Reason);
        Assert.False(run.InputSent);
        Assert.Contains("first run typed into an empty field", run.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);
        Assert.Equal("the owner's own notes", rig.Screen.Values["Name"]);
    }

    [Fact]
    public async Task From_the_users_words_it_comes_back_as_needs_brain_with_that_reason_and_nothing_sent()
    {
        var (rig, name) = await Learn(string.Empty);
        using var _ = rig;
        for (var i = 0; i < rig.Config.Learning.PromoteAfter; i++)
        {
            rig.Screen.Values["Name"] = string.Empty;            // each clean replay starts from an empty field, as when it was learned
            Assert.Equal(ActStatus.Confirmed, (await rig.Replay(name, new() { ["name"] = "x" })).Status);
        }

        Assert.True(rig.State(name).Trusted);
        rig.Screen.Values["Name"] = "left over";
        rig.Screen.Acts.Clear();

        var text = await rig.Ask("fill the form for carol");

        Assert.StartsWith("needs_brain reason=recipe_not_ready input_sent=false", text, StringComparison.Ordinal);
        Assert.Contains("the field already has text; the first run typed into an empty field", text, StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);
    }

    [Fact]
    public async Task Learned_in_an_empty_field_and_the_field_is_empty_now_types_as_usual()
    {
        var (rig, name) = await Learn(string.Empty);
        using var _ = rig;
        rig.Screen.Values["Name"] = string.Empty;

        var run = await rig.Replay(name, new() { ["name"] = "alice" });

        Assert.Equal(ActStatus.Confirmed, run.Status);
        Assert.Equal("alice", rig.Screen.Values["Name"]);
    }

    [Fact]
    public async Task Learned_in_a_field_that_already_had_text_adds_to_it_as_before()
    {
        var (rig, name) = await Learn("Dear ");
        using var _ = rig;
        rig.Screen.Values["Name"] = "Hello ";

        var run = await rig.Replay(name, new() { ["name"] = "alice" });

        Assert.Equal(ActStatus.Confirmed, run.Status);
        Assert.Equal("Hello alice", rig.Screen.Values["Name"]);
    }

    [Fact]
    public async Task When_the_field_could_not_be_read_nothing_changes_and_old_files_still_load()
    {
        using var rig = new LearningRig();                       // ProbeFields off: like an app OmniWin cannot read
        var name = await rig.LearnForm();
        var (_, workflow) = rig.Workflows.Store.Load(name);
        Assert.Null(workflow!.Steps[0].TargetEmptyBefore);
        rig.Screen.Values["Name"] = "anything";
        Assert.Equal(ActStatus.Confirmed, (await rig.Replay(name, new() { ["name"] = "x" })).Status);

        // A version 1 file written before contract 2.6 has no target_empty_before at all.
        var file = Path.Combine(rig.WorkflowsDir, name + ".json");
        var text = File.ReadAllText(file);
        Assert.Contains("\"target_empty_before\": null", text, StringComparison.Ordinal);
        var old = Path.Combine(rig.WorkflowsDir, "older-one.json");
        File.WriteAllText(old, text.Replace(",\r\n      \"target_empty_before\": null", string.Empty, StringComparison.Ordinal)
            .Replace(",\n      \"target_empty_before\": null", string.Empty, StringComparison.Ordinal).Replace("\"" + name + "\"", "\"older-one\"", StringComparison.Ordinal));
        Assert.DoesNotContain("target_empty_before", File.ReadAllText(old), StringComparison.Ordinal);
        var (loaded, oldWorkflow) = rig.Workflows.Store.Load("older-one");
        Assert.Equal(StoreStatus.Ok, loaded.Status);
        Assert.Null(oldWorkflow!.Steps[0].TargetEmptyBefore);
    }

    [Fact]
    public async Task Only_yes_or_no_is_stored_never_the_fields_text()
    {
        var (rig, name) = await Learn("zanzibar-secret-text ");
        using var _ = rig;

        var (_, workflow) = rig.Workflows.Store.Load(name);
        Assert.False(workflow!.Steps[0].TargetEmptyBefore);
        foreach (var file in Directory.EnumerateFiles(rig.WorkflowsDir).Concat(Directory.EnumerateFiles(rig.RecipesDir)))
        {
            Assert.DoesNotContain("zanzibar", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }
}
