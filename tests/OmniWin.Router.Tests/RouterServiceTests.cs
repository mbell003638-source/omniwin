using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Router;

namespace OmniWin.Router.Tests;

/// <summary>omniwin_do against fakes: what it sends, what it refuses, and that it never sends twice.</summary>
public sealed class RouterServiceTests
{
    private readonly FakeSystem _system = new();
    private readonly FakeDesktop _desktop = new();
    private readonly FakeWorkflows _workflows = new();

    private RouterService Router(OmniWinConfig? config = null, IIntentClassifier? classifier = null) =>
        new(_system, _desktop, _workflows, config ?? new OmniWinConfig(), new NullMetrics(), classifier);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pause_runs_the_media_tool_once_and_reports_its_result_line()
    {
        var text = await Router().DoAsync("Hey, pause the music please", Ct);

        Assert.Equal(["pause"], _system.Media);
        Assert.StartsWith("status=confirmed input_sent=true", text, StringComparison.Ordinal);
        Assert.Contains("retry=done", text, StringComparison.Ordinal);
        Assert.Contains("routed rule=media.pause", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Volume_up_reads_the_volume_then_sets_it_ten_higher()
    {
        await Router().DoAsync("volume up", Ct);

        Assert.Equal(1, _system.AudioReads);
        Assert.Equal([(50, (bool?)null)], _system.AudioSets);
    }

    [Fact]
    public async Task Volume_never_goes_past_the_ends()
    {
        _system.Audio = new AudioState(97, false, "Speakers");
        await Router().DoAsync("volume up by 20", Ct);
        _system.Audio = new AudioState(5, false, "Speakers");
        await Router().DoAsync("volume down", Ct);

        Assert.Equal([(100, (bool?)null), (0, (bool?)null)], _system.AudioSets);
    }

    [Fact]
    public async Task Toggle_mute_reads_the_state_and_sets_the_opposite_once()
    {
        _system.Audio = new AudioState(30, true, "Speakers");
        await Router().DoAsync("toggle mute", Ct);

        Assert.Equal([((int?)null, (bool?)false)], _system.AudioSets);
    }

    [Theory]
    [InlineData("delete my downloads folder", "risky_words")]
    [InlineData("restart the computer", "risky_words")]
    [InlineData("find me a good thriller", "no_rule")]
    [InlineData("", "empty")]
    public async Task A_request_for_the_brain_sends_nothing_and_says_why(string text, string code)
    {
        var reply = await Router().DoAsync(text, Ct);

        Assert.StartsWith($"needs_brain reason={code} input_sent=false retry=safe", reply, StringComparison.Ordinal);
        Assert.Equal(0, _system.Total);
        Assert.Equal(0, _system.AudioReads);
    }

    [Fact]
    public async Task Switch_to_a_window_with_exactly_one_match_focuses_that_window()
    {
        await Router().DoAsync("switch to spotify", Ct);

        var action = Assert.Single(_system.WindowActions);
        Assert.Equal(("w3", "focus"), (action.Window, action.Action));
    }

    [Fact]
    public async Task Minimize_this_window_acts_on_the_foreground_window()
    {
        await Router().DoAsync("minimize this window", Ct);

        var action = Assert.Single(_system.WindowActions);
        Assert.Equal(("w1", "minimize"), (action.Window, action.Action));
    }

    [Fact]
    public async Task Two_windows_that_match_are_never_guessed()
    {
        _desktop.Windows.Add(new("w4", 104, 4, "notepad.exe", "Second - Notepad", false, false, false, null, null));

        var reply = await Router().DoAsync("switch to notepad", Ct);

        Assert.StartsWith("needs_brain reason=several_windows", reply, StringComparison.Ordinal);
        Assert.Empty(_system.WindowActions);
    }

    [Fact]
    public async Task A_name_that_matches_no_window_is_left_to_the_brain()
    {
        var reply = await Router().DoAsync("switch to the second monitor", Ct);

        Assert.StartsWith("needs_brain reason=no_window", reply, StringComparison.Ordinal);
        Assert.Empty(_system.WindowActions);
    }

    [Fact]
    public async Task Open_launches_a_Start_menu_app_by_its_exact_name_and_refuses_unknown_names()
    {
        await Router().DoAsync("open notepad", Ct);
        var unknown = await Router().DoAsync("open the budget spreadsheet", Ct);

        Assert.Equal(["Notepad"], _system.Launches);
        Assert.StartsWith("needs_brain reason=no_app", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_without_parameters_runs_and_one_with_parameters_goes_to_the_brain()
    {
        var ran = await Router().DoAsync("run workflow morning setup", Ct);
        var needs = await Router().DoAsync("run workflow save draft", Ct);

        Assert.Equal(["morning_setup"], _workflows.Runs);
        Assert.Contains("routed rule=workflow.run", ran, StringComparison.Ordinal);
        Assert.StartsWith("needs_brain reason=needs_params", needs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_are_answered_with_input_sent_false()
    {
        var time = await Router().DoAsync("what time is it", Ct);

        Assert.StartsWith("status=confirmed input_sent=false", time, StringComparison.Ordinal);
        Assert.Contains("time fake", time, StringComparison.Ordinal);
        Assert.Equal(0, _system.Total);
    }

    [Fact]
    public async Task The_router_can_be_switched_off()
    {
        var reply = await Router(new OmniWinConfig { Router = new RouterConfig { Enabled = false } }).DoAsync("pause", Ct);

        Assert.StartsWith("needs_brain reason=router_off", reply, StringComparison.Ordinal);
        Assert.Empty(_system.Media);
    }

    // ---------------------------------------------------------------- stage B (the classifier is a fake)

    [Fact]
    public async Task The_classifier_is_not_asked_when_a_rule_matched_or_the_request_is_risky()
    {
        var classifier = new FakeClassifier(new IntentGuess("media_pause", 0.99, 10));
        var router = Router(classifier: classifier);

        await router.DoAsync("pause", Ct);
        await router.DoAsync("delete my files", Ct);

        Assert.Equal(0, classifier.Calls);
    }

    [Fact]
    public async Task A_confident_fast_classifier_answer_runs_the_command_with_values_read_by_rules()
    {
        var classifier = new FakeClassifier(new IntentGuess("volume_set", 0.97, 20));

        await Router(classifier: classifier).DoAsync("make the sound about 35 if you could", Ct);

        Assert.Equal(1, classifier.Calls);
        Assert.Equal([(35, (bool?)null)], _system.AudioSets);
    }

    [Theory]
    [InlineData(0.50, 20)]     // not sure enough
    [InlineData(0.99, 5000)]   // too slow
    public async Task A_classifier_answer_that_is_unsure_or_slow_is_not_run(double confidence, double latency)
    {
        var classifier = new FakeClassifier(new IntentGuess("media_pause", confidence, latency));

        var reply = await Router(classifier: classifier).DoAsync("could you hold the tune", Ct);

        Assert.StartsWith("needs_brain reason=low_confidence", reply, StringComparison.Ordinal);
        Assert.Equal(0, _system.Total);
    }

    [Fact]
    public async Task A_classifier_intent_whose_number_cannot_be_read_is_not_run()
    {
        var classifier = new FakeClassifier(new IntentGuess("volume_set", 0.99, 10));

        var reply = await Router(classifier: classifier).DoAsync("make it nice and loud", Ct);

        Assert.StartsWith("needs_brain reason=no_slots", reply, StringComparison.Ordinal);
        Assert.Empty(_system.AudioSets);
    }

    [Fact]
    public async Task The_classifier_saying_none_means_the_brain()
    {
        var reply = await Router(classifier: new FakeClassifier(new IntentGuess("none", 0.99, 10))).DoAsync("what is the meaning of life", Ct);

        Assert.StartsWith("needs_brain reason=no_rule", reply, StringComparison.Ordinal);
    }
}
