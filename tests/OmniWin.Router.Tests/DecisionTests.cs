using OmniWin.Safety;
using OmniWin.Server.Learning;

namespace OmniWin.Router.Tests;

/// <summary>A stand-in decision model with scripted answers. It records exactly what it was sent.</summary>
internal sealed class ScriptedModel : IDecisionModel
{
    public string Name => "scripted";

    public bool IsRemote { get; init; }

    public int Calls { get; private set; }

    public List<(string Request, IReadOnlyList<DecisionOption> Options)> Sent { get; } = [];

    /// <summary>Picks handed back, best first. Null: the model has no answer.</summary>
    public Func<string, IReadOnlyList<DecisionOption>, (string Id, double Confidence)[]?> Answer { get; set; } = (_, _) => null;

    public TimeSpan Delay { get; set; }

    public Exception? Throw { get; set; }

    public async Task<DecisionAnswer?> RankAsync(string request, IReadOnlyList<DecisionOption> options, CancellationToken ct)
    {
        Calls++;
        Sent.Add((request, options));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Throw is not null) throw Throw;
        return Answer(request, options) is { } picks ? new DecisionAnswer([.. picks.Select(p => new DecisionPick(p.Id, p.Confidence))]) : null;
    }
}

/// <summary>
/// The optional decision stage (contract 2.6) with a scripted stand-in: off means never called; it acts only inside every rule;
/// every other case ends at the AI; shadow mode never runs anything and keeps an honest score; a wrong pick forces shadow.
/// </summary>
public sealed class DecisionTests
{
    private static OmniWinConfig Config(string mode, bool allowRemote = false, int timeoutMs = 1500, int maxLatencyMs = 400) => new()
    {
        Decision = new DecisionConfig { Provider = "scripted", Mode = mode, AllowRemote = allowRemote, TimeoutMs = timeoutMs, MaxLatencyMs = maxLatencyMs },
    };

    private static async Task<(LearningRig Rig, string Form, string Morning)> Build(OmniWinConfig config, ScriptedModel model)
    {
        var rig = new LearningRig(config, model);
        var form = await rig.LearnForm();
        await rig.Trust(form);
        await rig.Ask("start my morning setup");
        await rig.Do(("Agree", "toggle", null), ("Apply", "click", null));
        var morning = LearningRig.Token(rig.Learning.TaskDone(true, null, null, null), "recipe=");
        await rig.Trust(morning);
        rig.Screen.Acts.Clear();
        model.Sent.Clear();
        return (rig, form, morning);
    }

    private static (string, double)[] Pick(string id, double confidence, string second = "none", double secondConfidence = 0.01) =>
        [(id, confidence), (second, secondConfidence)];

    [Fact]
    public async Task With_no_provider_the_model_is_never_called_and_status_says_off()
    {
        var model = new ScriptedModel { Answer = (_, _) => Pick("none", 0.99) };
        using var rig = new LearningRig(new OmniWinConfig(), model);
        await rig.LearnForm();

        await rig.Ask("get the form done for alice");

        Assert.Equal(0, model.Calls);
        Assert.Equal("off", rig.Decision.State);
        Assert.False(File.Exists(Path.Combine(rig.RecipesDir, "decision-events.jsonl")));
    }

    [Fact]
    public async Task A_model_that_is_not_on_this_PC_is_never_called_unless_the_owner_allowed_it()
    {
        var model = new ScriptedModel { IsRemote = true, Answer = (_, _) => Pick("none", 0.99) };
        using var blocked = new LearningRig(Config("act"), model);
        await blocked.Ask("get the form done for alice");
        Assert.Equal(0, model.Calls);
        Assert.Equal("unavailable", blocked.Decision.State);
        Assert.Contains("allow_remote", blocked.Decision.Detail!, StringComparison.Ordinal);

        using var allowed = new LearningRig(Config("act", allowRemote: true), model);
        await allowed.Ask("get the form done for alice");
        Assert.Equal(1, model.Calls);
        Assert.Contains("(remote)", allowed.Decision.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_act_mode_a_confident_pick_of_a_trusted_recipe_runs_it_with_the_value_taken_from_the_request()
    {
        var model = new ScriptedModel();
        var (rig, form, _) = await Build(Config("act"), model);
        using var _1 = rig;
        model.Answer = (_, _) => Pick("recipe:" + form, 0.98);

        var text = await rig.Ask("the form for Alice please");

        Assert.Contains($"routed rule=decision.recipe.{form}", text, StringComparison.Ordinal);
        Assert.Equal(("Name", "type", "Alice"), rig.Screen.Acts[0]);
        Assert.Equal(3, rig.Screen.Acts.Count);
    }

    [Fact]
    public async Task A_recipe_without_parameters_runs_for_any_wording_the_model_is_sure_about()
    {
        var model = new ScriptedModel();
        var (rig, _, morning) = await Build(Config("act"), model);
        using var _1 = rig;
        model.Answer = (_, _) => Pick("recipe:" + morning, 0.99);

        var text = await rig.Ask("get my usual things going");

        Assert.Contains($"routed rule=decision.recipe.{morning}", text, StringComparison.Ordinal);
        Assert.Equal(2, rig.Screen.Acts.Count);
    }

    [Fact]
    public async Task A_confident_pick_of_a_quick_command_runs_it_through_the_normal_tool()
    {
        var model = new ScriptedModel { Answer = (_, _) => Pick("intent:volume_set", 0.99) };
        var (rig, _, _) = await Build(Config("act"), model);
        using var _1 = rig;

        var text = await rig.Ask("make the speakers 35 percent loud");

        Assert.Equal([(35, (bool?)null)], rig.System.AudioSets);
        Assert.Contains("routed rule=decision.volume_set", text, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, double, double, string> BrainCases => new()
    {
        // request, pick, confidence, second, expected reason
        { "get the form done for alice", "FORM", 0.80, 0.01, "low_confidence" },            // below the threshold
        { "get the form done for alice", "FORM", 0.97, 0.80, "low_confidence" },            // no clear lead
        { "get the form done for alice", "none", 0.99, 0.00, "no_rule" },                   // the model says: not for me
        { "handle bob then alice form", "FORM", 0.99, 0.00, "no_slots" },                   // two candidate values
        { "the form", "FORM", 0.99, 0.00, "no_slots" },                                     // no value in the request
        { "make it loud", "intent:volume_set", 0.99, 0.00, "no_slots" },                    // no number to read
    };

    [Theory]
    [MemberData(nameof(BrainCases))]
    public async Task Anything_short_of_every_rule_goes_to_the_AI_with_nothing_sent(string request, string pick, double confidence, double second, string reason)
    {
        var model = new ScriptedModel();
        var (rig, form, morning) = await Build(Config("act"), model);
        using var _1 = rig;
        var id = pick == "FORM" ? "recipe:" + form : pick;
        model.Answer = (_, _) => [(id, confidence), ("recipe:" + morning, second)];

        var text = await rig.Ask(request);

        Assert.StartsWith($"needs_brain reason={reason} input_sent=false", text, StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);
        Assert.Equal(0, rig.System.Total);
    }

    [Fact]
    public async Task A_slow_failing_or_silent_model_costs_at_most_the_timeout_and_the_AI_gets_the_request()
    {
        var model = new ScriptedModel();
        var (rig, form, _) = await Build(Config("act", timeoutMs: 150, maxLatencyMs: 50), model);
        using var _1 = rig;

        model.Delay = TimeSpan.FromSeconds(30);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.StartsWith("needs_brain reason=model_unavailable", await rig.Ask("get the form done for alice"), StringComparison.Ordinal);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");

        model.Delay = TimeSpan.Zero;
        model.Throw = new HttpRequestException("down");
        Assert.StartsWith("needs_brain reason=model_unavailable", await rig.Ask("get the form done for alice"), StringComparison.Ordinal);

        model.Throw = null;
        model.Answer = (_, _) => null;
        Assert.StartsWith("needs_brain reason=model_unavailable", await rig.Ask("get the form done for alice"), StringComparison.Ordinal);

        model.Answer = (_, _) => Pick("recipe:made-up-by-the-model", 0.99);                   // not one of the options it was given
        Assert.StartsWith("needs_brain reason=low_confidence", await rig.Ask("get the form done for alice"), StringComparison.Ordinal);   // the invented pick is dropped

        model.Delay = TimeSpan.FromMilliseconds(90);                                          // answered, but slower than max_latency_ms
        model.Answer = (_, _) => Pick("recipe:" + form, 0.99);
        Assert.StartsWith("needs_brain reason=low_confidence", await rig.Ask("get the form done for alice"), StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);
    }

    [Fact]
    public async Task Risky_long_and_multi_line_requests_are_never_shown_to_the_model()
    {
        var model = new ScriptedModel { Answer = (_, _) => Pick("none", 0.99) };
        var (rig, _, _) = await Build(Config("act"), model);
        using var _1 = rig;

        await rig.Ask("delete the form for alice");
        await rig.Ask("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone");
        await rig.Ask("the form for alice\nand then wipe the disk");

        Assert.Empty(model.Sent);
    }

    [Fact]
    public async Task The_model_is_sent_only_the_request_wording_and_the_option_labels()
    {
        var model = new ScriptedModel { Answer = (_, _) => Pick("none", 0.99) };
        var (rig, form, morning) = await Build(Config("act"), model);
        using var _1 = rig;

        await rig.Ask("Get the form done for Alice!");

        var (request, options) = Assert.Single(model.Sent);
        Assert.Equal("get the form done for alice", request);
        Assert.Contains(options, o => o.Id == "recipe:" + form && o.Label == "fill the form for bob" && o.Examples.SequenceEqual(["fill the form for {name}"]));
        Assert.Contains(options, o => o.Id == "recipe:" + morning);
        Assert.Contains(options, o => o.Id == "intent:volume_set");
        Assert.Equal("none", options[^1].Id);
        // Nothing about the screen: no control names, window titles, process names or typed text.
        var everything = string.Join('\n', options.Select(o => o.Id + "|" + o.Label + "|" + string.Join('|', o.Examples)));
        foreach (var secret in new[] { "Agree", "Apply", "formapp", "Form\"", "e1" }) Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shadow_mode_never_runs_a_pick_and_scores_it_against_what_the_AI_really_did()
    {
        var model = new ScriptedModel();
        var (rig, form, morning) = await Build(Config("shadow"), model);
        using var _1 = rig;

        // Right: the model picked the form recipe, and the AI then ran exactly that recipe.
        model.Answer = (_, _) => Pick("recipe:" + form, 0.98);
        var asked = await rig.Ask("get the form done for alice");
        Assert.StartsWith("needs_brain reason=no_rule", asked, StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);                                               // shadow never acts
        await rig.Replay(form, new() { ["name"] = "alice" });
        rig.Learning.TaskDone(true, null, null, null);

        // Wrong: it picked the morning recipe, but the AI ran the form recipe.
        model.Answer = (_, _) => Pick("recipe:" + morning, 0.98);
        await rig.Ask("sort out the form for carol");
        await rig.Replay(form, new() { ["name"] = "carol" });
        rig.Learning.TaskDone(true, null, null, null);

        // Not judged: an unsure answer.
        model.Answer = (_, _) => Pick("recipe:" + form, 0.50);
        await rig.Ask("something about a form for dave");
        rig.Learning.TaskDone(false, null, null, null);

        Assert.Equal("shadow", rig.Decision.State);
        Assert.Contains("2 confident shadow picks judged, 1 wrong, 0 acted", rig.Decision.Detail!, StringComparison.Ordinal);
        var log = File.ReadAllText(Path.Combine(rig.RecipesDir, "decision-events.jsonl"));
        foreach (var word in new[] { "alice", "carol", "dave", "form done", "sort out" }) Assert.DoesNotContain(word, log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reported_wrong_pick_forces_shadow_until_the_owner_edits_the_config()
    {
        var model = new ScriptedModel();
        var (rig, form, _) = await Build(Config("act"), model);
        using var _1 = rig;
        model.Answer = (_, _) => Pick("recipe:" + form, 0.98);
        rig.ConfigEditedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        Assert.Contains("routed rule=decision.recipe.", await rig.Ask("the form for Alice please"), StringComparison.Ordinal);
        rig.Learning.Manage(form, "wrong_pick");
        rig.Learning.Manage(form, "right");
        rig.Learning.Manage(form, "right");
        rig.Learning.Manage(form, "right");                                          // the recipe itself is trusted again
        rig.Screen.Acts.Clear();

        Assert.Equal("shadow_forced", rig.Decision.State);
        Assert.StartsWith("needs_brain", await rig.Ask("the form for Bea please"), StringComparison.Ordinal);
        Assert.Empty(rig.Screen.Acts);

        rig.ConfigEditedAt = DateTimeOffset.UtcNow.AddMinutes(5);                    // the owner looked and saved config.json
        Assert.Equal("act", rig.Decision.State);
    }

    [Fact]
    public async Task The_models_recipes_are_listed_first_for_the_AI_when_it_is_not_sure()
    {
        var model = new ScriptedModel();
        var (rig, form, morning) = await Build(Config("act"), model);
        using var _1 = rig;
        model.Answer = (_, _) => [("recipe:" + morning, 0.6), ("recipe:" + form, 0.3)];

        var lines = (await rig.Ask("do my usual thing")).Split('\n').Where(l => l.StartsWith("recipe: ", StringComparison.Ordinal)).ToList();

        Assert.StartsWith($"recipe: {morning}(", lines[0], StringComparison.Ordinal);
        Assert.StartsWith($"recipe: {form}(", lines[1], StringComparison.Ordinal);
    }
}
