using System.Text.Json;
using OmniWin.Core;
using OmniWin.Server.Router;

namespace OmniWin.Router.Tests;

/// <summary>
/// The strict phrase table against the 600+ labelled commands in tools\laya\eval_set.jsonl. The rule that matters is safety: whatever
/// the table matches must be the labelled intent, and a request labelled "none" must never become a command that could run. How much
/// of the set it covers is only reported (the brain handles the rest).
/// </summary>
public sealed class PhraseRouterTests
{
    private sealed record Item(string Text, string Intent, JsonElement Expect);

    // The catalogue the labelled set talks about. "none" requests that parse as "open X" or "switch to X" must fail to resolve here.
    private static readonly string[] Apps = ["notepad", "calculator", "paint", "edge", "chrome", "spotify", "word", "excel", "settings", "file explorer", "terminal", "vlc"];
    private static readonly string[] WindowNames = ["chrome", "edge", "notepad", "spotify", "explorer", "code", "word", "terminal", "calculator"];
    private static readonly string[] Workflows = ["morning setup", "save draft", "open report", "daily notes", "backup note"];

    private static List<Item> EvalSet() => Load("eval_set.jsonl");

    /// <summary>Hand-written natural paraphrases and near-miss sentences that the table is not shaped around.</summary>
    private static List<Item> HardSet() => Load("eval_hard.jsonl");

    private static List<Item> Load(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "tools", "laya", name);
            if (!File.Exists(file)) continue;
            return File.ReadAllLines(file).Where(l => l.Trim().Length > 0).Select(l =>
            {
                using var doc = JsonDocument.Parse(l);
                var root = doc.RootElement;
                return new Item(root.GetProperty("text").GetString()!, root.GetProperty("intent").GetString()!,
                    root.TryGetProperty("expect", out var e) ? e.Clone() : default);
            }).ToList();
        }

        throw new FileNotFoundException("tools\\laya\\" + name + " was not found above " + AppContext.BaseDirectory);
    }

    internal static string IntentOf(RouteCommand command) => command switch
    {
        MediaCommand m => m.Action switch
        {
            "play" => "media_play", "pause" => "media_pause", "play_pause" => "media_toggle", "next" => "media_next", "previous" => "media_previous", _ => "?",
        },
        VolumeSetCommand => "volume_set",
        VolumeStepCommand s => s.Delta > 0 ? "volume_up" : "volume_down",
        MuteCommand m => m.Mute ? "mute" : "unmute",
        MuteToggleCommand => "mute_toggle",
        WindowCommand w => "window_" + w.Action,
        LaunchCommand => "app_launch",
        WorkflowCommand => "workflow_run",
        ReadCommand r => "read_" + r.Kind,
        _ => "?",
    };

    /// <summary>Would this command find a real target in the catalogue the labelled set uses?</summary>
    private static bool Resolves(RouteCommand command)
    {
        var apps = Apps.Select(a => new InstalledApp(a, a, null)).ToList();
        var windows = WindowNames.Select((n, i) => new WindowInfo("w" + (i + 1), i + 1, i + 1, n + ".exe", n, false, false, false, null, null)).ToList();
        var workflows = Workflows.Select(w => new OmniWin.Workflows.WorkflowSummary(w, "", 1, [], DateTimeOffset.UtcNow)).ToList();
        return command switch
        {
            LaunchCommand l => RouterService.ResolveApp(apps, l.AppName) is not null,
            WindowCommand { Target: { } t } => RouterService.ResolveWindow(windows, t).Window is not null,
            WorkflowCommand w => RouterService.ResolveWorkflow(workflows, w.Name) is not null,
            _ => true,
        };
    }

    [Fact]
    public void Whatever_the_table_matches_is_the_labelled_intent_and_none_requests_never_resolve_to_a_target()
    {
        var wrong = new List<string>();
        foreach (var item in EvalSet().Concat(HardSet()))
        {
            var parsed = PhraseRouter.Parse(item.Text);
            if (parsed.Command is not { } command) continue;
            if (item.Intent == "none")
            {
                // Only the kinds that look a name up can match a "none" sentence, and then the name must not exist.
                if (command is not (LaunchCommand or WindowCommand or WorkflowCommand) || Resolves(command))
                    wrong.Add($"none -> {IntentOf(command)} for \"{item.Text}\"");
            }
            else if (IntentOf(command) != item.Intent)
            {
                // A name-taking rule that disagrees with the label is harmless only if it finds no real target (so nothing would run).
                if (command is not (LaunchCommand or WindowCommand or WorkflowCommand) || Resolves(command))
                    wrong.Add($"{item.Intent} -> {IntentOf(command)} for \"{item.Text}\"");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(40)));
    }

    [Fact]
    public void The_table_reads_the_numbers_and_names_the_labelled_set_expects()
    {
        var wrong = new List<string>();
        foreach (var item in EvalSet().Where(i => i.Expect.ValueKind == JsonValueKind.Object))
        {
            if (PhraseRouter.Parse(item.Text).Command is not { } command) continue;
            foreach (var expected in item.Expect.EnumerateObject())
            {
                var ok = (expected.Name, command) switch
                {
                    ("percent", VolumeSetCommand v) => v.Percent == expected.Value.GetInt32(),
                    ("target", WindowCommand w) => w.Target == expected.Value.GetString(),
                    ("app", LaunchCommand l) => l.AppName == expected.Value.GetString(),
                    ("name", WorkflowCommand f) => f.Name == expected.Value.GetString(),
                    _ => false,
                };
                if (!ok) wrong.Add($"{expected.Name}={expected.Value} not read from \"{item.Text}\" ({command})");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(40)));
    }

    [Fact]
    public void The_table_covers_at_least_half_of_the_labelled_commands()
    {
        var commands = EvalSet().Where(i => i.Intent != "none").ToList();
        var covered = commands.Count(i => PhraseRouter.Parse(i.Text).Command is not null);

        // The numbers are saved so the coverage is a reported fact, not only a floor: what the table answers on its own, per intent,
        // and a sample of what it leaves to the brain (or to the Laya stage).
        var perIntent = commands.GroupBy(i => i.Intent).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new
        {
            commands = g.Count(),
            matched = g.Count(i => PhraseRouter.Parse(i.Text).Command is not null),
        });
        var missed = commands.Where(i => PhraseRouter.Parse(i.Text).Command is null).Select(i => new { i.Intent, i.Text }).Take(60).ToList();
        var root = FindRepoRoot();
        var path = Path.Combine(root, "artifacts", "benchmarks", "router-phrase-coverage-v1.json");
        for (var v = 2; File.Exists(path); v++) path = Path.Combine(root, "artifacts", "benchmarks", $"router-phrase-coverage-v{v}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            what = "strict phrase table (stage A of omniwin_do) against tools/laya/eval_set.jsonl",
            labelled_commands = commands.Count,
            matched_by_table = covered,
            coverage = Math.Round(covered / (double)commands.Count, 3),
            per_intent = perIntent,
            first_unmatched = missed,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(covered >= commands.Count / 2, $"covered {covered} of {commands.Count}");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OmniWin.sln"))) return dir.FullName;
        }

        throw new DirectoryNotFoundException("OmniWin.sln was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_hard_set_is_saved_with_how_much_of_it_the_table_answers_on_its_own()
    {
        var hard = HardSet();
        var commands = hard.Where(i => i.Intent != "none").ToList();
        var negatives = hard.Where(i => i.Intent == "none").ToList();
        var matchedCommands = commands.Count(i => PhraseRouter.Parse(i.Text).Command is not null);
        var runnableNegatives = negatives.Where(i => PhraseRouter.Parse(i.Text).Command is { } c && Resolves(c)).Select(i => i.Text).ToList();
        var root = FindRepoRoot();
        var path = Path.Combine(root, "artifacts", "benchmarks", "router-phrase-coverage-hard-v1.json");
        for (var v = 2; File.Exists(path); v++) path = Path.Combine(root, "artifacts", "benchmarks", $"router-phrase-coverage-hard-v{v}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            what = "strict phrase table against hand-written paraphrases and near-miss sentences (tools/laya/eval_hard.jsonl)",
            labelled_commands = commands.Count,
            matched_by_table = matchedCommands,
            coverage = Math.Round(matchedCommands / (double)commands.Count, 3),
            near_miss_sentences = negatives.Count,
            near_misses_that_would_run = runnableNegatives,
            left_to_the_brain_or_laya = commands.Where(i => PhraseRouter.Parse(i.Text).Command is null).Select(i => new { i.Intent, i.Text }).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true }));

        // The one rule that is a pass/fail: no near-miss sentence may become a command that finds a real target.
        Assert.Empty(runnableNegatives);
    }

    [Theory]
    [InlineData("volume 250")]
    [InlineData("set the volume to 101")]
    public void A_number_above_100_is_never_a_volume(string text) =>
        Assert.Null(PhraseRouter.Parse(text).Command);

    [Theory]
    [InlineData("delete my downloads", "risky_words")]
    [InlineData("restart the pc", "risky_words")]
    [InlineData("send this to anna", "risky_words")]
    [InlineData("install chrome", "risky_words")]
    [InlineData("pay the electricity bill", "risky_words")]
    [InlineData("restore my deleted files", "risky_words")]
    [InlineData("installing chrome now", "risky_words")]
    [InlineData("the payment is due", "risky_words")]
    [InlineData("", "empty")]
    [InlineData("tell me a joke about computers and the weather today in the city of paris france europe", "too_long")]
    [InlineData("tell me a joke", "no_rule")]
    public void Requests_for_the_brain_come_back_with_the_reason(string text, string code)
    {
        var parsed = PhraseRouter.Parse(text);
        Assert.Null(parsed.Command);
        Assert.Equal(code, parsed.Why!.Code);
    }

    [Theory]
    [InlineData("Hey, could you please pause the music?", "pause the music")]
    [InlineData("What's the time", "whats the time")]
    [InlineData("  VOLUME   30  ", "volume 30")]
    public void Normalize_removes_politeness_punctuation_and_case(string text, string expected) =>
        Assert.Equal(expected, PhraseRouter.Normalize(text));

    [Fact]
    public void A_toggle_is_only_run_when_the_user_asked_for_a_toggle()
    {
        Assert.Equal("play", ((MediaCommand)PhraseRouter.Parse("play").Command!).Action);
        Assert.Equal("pause", ((MediaCommand)PhraseRouter.Parse("pause").Command!).Action);
        Assert.Equal("play_pause", ((MediaCommand)PhraseRouter.Parse("toggle playback").Command!).Action);
    }

    [Fact]
    public void FromIntent_reads_values_with_rules_and_refuses_when_it_cannot()
    {
        Assert.Equal(55, ((VolumeSetCommand)PhraseRouter.FromIntent("volume_set", "make the sound 55 please")!).Percent);
        Assert.Null(PhraseRouter.FromIntent("volume_set", "make it nice and loud"));
        Assert.Null(PhraseRouter.FromIntent("volume_set", "volume 20 or maybe 30"));
        Assert.Null(PhraseRouter.FromIntent("window_focus", "switch to"));
        Assert.Equal("notepad", ((LaunchCommand)PhraseRouter.FromIntent("app_launch", "could you open notepad")!).AppName);
        Assert.Null(PhraseRouter.FromIntent("media_pause", "delete everything and pause"));
        Assert.Null(PhraseRouter.FromIntent("none", "pause"));
        Assert.All(PhraseRouter.Intents.Where(i => i != "none"), i => Assert.NotNull(i));
    }
}
