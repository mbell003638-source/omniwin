using System.Diagnostics;
using System.Globalization;
using System.Text;
using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Workflows;

namespace OmniWin.Server.Router;

/// <summary>The saved workflows the router may run: their names and a way to run one (the real one is <see cref="WorkflowCatalog"/>).</summary>
internal interface IWorkflowCatalog
{
    IReadOnlyList<WorkflowSummary> List();

    /// <summary>Runs the workflow with no parameters and returns its formatted result.</summary>
    Task<string> RunAsync(string name, CancellationToken ct);
}

internal sealed class WorkflowCatalog(WorkflowService workflows) : IWorkflowCatalog
{
    public IReadOnlyList<WorkflowSummary> List() => workflows.List();

    public async Task<string> RunAsync(string name, CancellationToken ct) =>
        WorkflowFormatter.Format(await workflows.RunWorkflowAsync(name, null, ct).ConfigureAwait(false));
}

/// <summary>A classifier's answer: one intent from <see cref="PhraseRouter.Intents"/>, its confidence (0..1) and how long it took.</summary>
internal sealed record IntentGuess(string Intent, double Confidence, double LatencyMs);

/// <summary>Stage B: a local model that names the intent of a sentence the phrase table did not match. It never supplies values.</summary>
internal interface IIntentClassifier
{
    Task<IntentGuess?> ClassifyAsync(string text, CancellationToken ct);
}

/// <summary>
/// <c>omniwin_do</c>: runs a simple command locally, without any model call, through OmniWin's own tools (so every check, the safety
/// gate and the stop key apply as always). Only an exact, unambiguous match is run; everything else comes back as
/// <c>needs_brain</c> with nothing sent, and the caller's own model handles it. A toggle is never repeated.
/// </summary>
internal sealed class RouterService(
    ISystemTools system,
    IDesktopEngine desktop,
    IWorkflowCatalog workflows,
    OmniWinConfig config,
    IMetrics metrics,
    IIntentClassifier? classifier = null,
    OmniWin.Server.Learning.LearningService? learning = null,
    OmniWin.Server.Learning.DecisionService? decision = null) : IComponentStatus
{
    // omniwin_status
    public string Component => "router";

    public string State => !config.Router.Enabled ? "off" : classifier is null ? "phrase" : "phrase+laya";

    public string? Detail => classifier is null
        ? "omniwin_do: phrase table only"
        : $"omniwin_do: phrase table, then Laya (threshold {config.Router.Laya.Threshold.ToString("0.##", CultureInfo.InvariantCulture)})";

    public async Task<string> DoAsync(string text, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        string Finish(string body)
        {
            metrics.Record("omniwin_do", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return body;
        }

        if (!config.Router.Enabled) return Finish(NeedsBrain("router_off", "The quick router is switched off in config.json."));

        var parsed = PhraseRouter.Parse(text);

        // Contract 2.6, speed 1: a trusted learned recipe whose wording matches this request exactly, as a whole sentence.
        if (parsed.Command is null && parsed.Why is { Code: "no_rule" } && await TryRecipeAsync(text, ct).ConfigureAwait(false) is { } learned)
        {
            return Finish(learned);
        }

        // Contract 2.6: the optional decision model, only when the owner wired one. It may run a trusted recipe or a quick command
        // when it clears every rule; in shadow mode it only watches. Everything else goes on to the AI with its suggestions.
        IReadOnlyList<string> suggested = [];
        if (parsed.Command is null && decision is not null && learning is not null && parsed.Why is { Code: "no_rule" })
        {
            var decided = await decision.DecideAsync(text, ct).ConfigureAwait(false);
            suggested = decided.Suggested;
            if (decided.Recipe is { } picked)
            {
                var outcome = await learning.RunAsync(picked.Recipe.Name, picked.Values, text, ct).ConfigureAwait(false);
                return Finish(outcome.Total == 0 || (outcome.StoppedAt == 1 && !outcome.InputSent)
                    ? Brain(text, "recipe_not_ready", NotReady(picked.Recipe.Name, outcome))
                    : WorkflowFormatter.Format(outcome) + $"\nrouted rule=decision.recipe.{picked.Recipe.Name}");
            }

            if (decided.Command is { } chosen)
            {
                var ran = await RunAsync(chosen, ct).ConfigureAwait(false);
                return Finish(ran.StartsWith("needs_brain", StringComparison.Ordinal) ? Enrich(text, ran, "no_rule") : ran);
            }

            if (decided.Reason is { } reason) return Finish(Brain(text, reason, decided.Detail ?? string.Empty, suggested));
        }

        var guess = (IntentGuess?)null;
        if (parsed.Command is null && classifier is not null && parsed.Why is { Code: "no_rule" })
        {
            // Stage B: the table did not match; ask the local model for the intent, then read the values with rules.
            try
            {
                guess = await classifier.ClassifyAsync(text, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                guess = null;
            }

            if (guess is { Intent: "none" })
            {
                return Finish(Brain(text, "no_rule", "No quick rule matches this request, and the local model says it is not a quick command."));
            }

            if (guess is not null)
            {
                var sure = guess.Confidence >= config.Router.Laya.Threshold && guess.LatencyMs <= config.Router.Laya.MaxLatencyMs;
                if (!sure)
                {
                    return Finish(Brain(text, "low_confidence",
                        $"The local model was not sure enough or not fast enough ({guess.Intent} at {guess.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}, {guess.LatencyMs.ToString("0", CultureInfo.InvariantCulture)} ms)."));
                }

                if (PhraseRouter.FromIntent(guess.Intent, text) is not { } fromModel)
                {
                    return Finish(Brain(text, "no_slots", $"The local model chose {guess.Intent}, but its number or name could not be read from the text."));
                }

                parsed = ParseOutcome.Matched(fromModel);
            }
        }

        if (parsed.Command is not { } command)
        {
            return Finish(Brain(text, parsed.Why!.Code, parsed.Why.Detail, suggested));
        }

        try
        {
            var ran = await RunAsync(command, ct).ConfigureAwait(false);
            if (!ran.StartsWith("needs_brain", StringComparison.Ordinal)) return Finish(ran);

            // The table read the sentence as a command it then could not carry out ("open invoice 42 in word": no such app), and
            // nothing was sent. A learned recipe for exactly this wording comes next; otherwise the AI takes over.
            return Finish(await TryRecipeAsync(text, ct).ConfigureAwait(false) ?? Enrich(text, ran, "no_rule"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failure before anything was sent (a read of the window or app list). Nothing may have reached an app.
            return Finish(NeedsBrain("router_error", "The quick router could not complete the request (" + ex.GetType().Name + "); nothing was sent."));
        }
    }

    private async Task<string> RunAsync(RouteCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case MediaCommand media:
                return Wrap(command, await system.MediaAsync(media.Action, ct).ConfigureAwait(false));

            case VolumeSetCommand set:
                return Wrap(command, await system.SetAudioAsync(set.Percent, null, ct).ConfigureAwait(false));

            case VolumeStepCommand step:
            {
                var now = await system.GetAudioAsync(ct).ConfigureAwait(false);
                var target = Math.Clamp(now.VolumePercent + step.Delta, 0, 100);
                return Wrap(command, await system.SetAudioAsync(target, null, ct).ConfigureAwait(false));
            }

            case MuteCommand mute:
                return Wrap(command, await system.SetAudioAsync(null, mute.Mute, ct).ConfigureAwait(false));

            case MuteToggleCommand:
            {
                var now = await system.GetAudioAsync(ct).ConfigureAwait(false);
                return Wrap(command, await system.SetAudioAsync(null, !now.Muted, ct).ConfigureAwait(false));
            }

            case WindowCommand window:
            {
                var windows = await desktop.ListWindowsAsync(ct).ConfigureAwait(false);
                var (target, why) = ResolveWindow(windows, window.Target);
                return target is null
                    ? NeedsBrain(why!.Code, why.Detail)
                    : Wrap(command, await system.WindowManageAsync(new WindowManageRequest(target.Id, window.Action), ct).ConfigureAwait(false));
            }

            case LaunchCommand launch:
            {
                var apps = await system.ListAppsAsync(null, ct).ConfigureAwait(false);
                var app = ResolveApp(apps, launch.AppName);
                return app is null
                    ? NeedsBrain("no_app", $"No Start-menu app is named \"{launch.AppName}\", or more than one is.")
                    : Wrap(command, await system.LaunchAppAsync(app.Name, null, null, ct).ConfigureAwait(false));
            }

            case WorkflowCommand workflow:
            {
                var summary = ResolveWorkflow(workflows.List(), workflow.Name);
                if (summary is null) return NeedsBrain("no_workflow", $"No saved workflow is named \"{workflow.Name}\", or more than one is.");
                if (summary.ParamNames.Count > 0) return NeedsBrain("needs_params", $"Workflow \"{summary.Name}\" needs values for {string.Join(", ", summary.ParamNames)}; the brain supplies them.");
                return await workflows.RunAsync(summary.Name, ct).ConfigureAwait(false) + $"\nrouted rule={command.Rule}";
            }

            case ReadCommand read:
            {
                string body;
                if (read.Kind == "volume")
                {
                    var audio = await system.GetAudioAsync(ct).ConfigureAwait(false);
                    body = $"volume={audio.VolumePercent} muted={(audio.Muted ? "true" : "false")} device={ResultFormatter.Quote(audio.DeviceName, 80)}";
                }
                else
                {
                    body = await system.SystemInfoAsync(read.Kind, ct).ConfigureAwait(false);
                }

                var line = ResultFormatter.Format(new ActResult(ActStatus.Confirmed, false, null, null, null, null, Timing.Total(0)));
                return line + $"\nrouted rule={command.Rule}\n{ResultFormatter.UntrustedHeader}\n{body}";
            }
        }

        return NeedsBrain("no_rule", "No quick rule matches this request.");
    }

    /// <summary>
    /// needs_brain for a request the AI will now do itself. With learning on, this opens the episode that task_done closes, and lists
    /// the saved recipes most like the request so the AI can run one with a single call instead of working step by step.
    /// </summary>
    private string Brain(string text, string code, string detail, IReadOnlyList<string>? suggested = null) =>
        Enrich(text, NeedsBrain(code, detail), code, suggested);

    /// <summary>Runs the one trusted recipe that matches this wording, or returns null when there is none.</summary>
    private async Task<string?> TryRecipeAsync(string text, CancellationToken ct)
    {
        if (learning is null) return null;
        if (learning.FindExact(text, out var several) is not { } match)
        {
            return several ? Brain(text, "ambiguous", "More than one learned recipe matches this wording.") : null;
        }

        var outcome = await learning.RunAsync(match.Recipe.Name, match.Values, text, ct).ConfigureAwait(false);
        if (outcome.Total == 0 || (outcome.StoppedAt == 1 && !outcome.InputSent))
        {
            return Brain(text, "recipe_not_ready",
                NotReady(match.Recipe.Name, outcome));
        }

        return WorkflowFormatter.Format(outcome) + $"\nrouted rule=recipe.{match.Recipe.Name}";
    }

    private static string NotReady(string recipe, RunOutcome outcome) => outcome.Reason == ReasonCodes.FieldNotEmpty
        ? $"The learned recipe {recipe} stopped at its first step: the field already has text; the first run typed into an empty field. Nothing was sent. Clear or edit the field yourself, or do the task step by step."
        : $"The learned recipe {recipe} could not start (its first control is not on screen); nothing was sent.";

    private string Enrich(string text, string body, string code, IReadOnlyList<string>? suggested = null)
    {
        if (learning is null || !config.Learning.Enabled || code == "empty") return body;
        var sb = new StringBuilder(body);
        // The decision model's suggestions come first when there are any; the word-overlap list fills the rest (three lines at most).
        var listed = new List<string>();
        foreach (var name in suggested ?? [])
        {
            if (listed.Count < 3 && learning.Store.Get(name, config.Learning.PromoteAfter) is { Active: true } recipe)
            {
                listed.Add(name);
                sb.Append('\n').Append(OmniWin.Server.Learning.LearningService.ShortlistLine(recipe));
            }
        }

        foreach (var recipe in learning.Shortlist(text))
        {
            if (listed.Count >= 3 || listed.Contains(recipe.Name, StringComparer.Ordinal)) continue;
            listed.Add(recipe.Name);
            sb.Append('\n').Append(OmniWin.Server.Learning.LearningService.ShortlistLine(recipe));
        }

        learning.OpenEpisode(text);
        if (code != "risky_words")
        {
            sb.Append("\nlearn: when the task is done and worked, call task_done(success=true) so it can be replayed next time (omniwin_guide topic=recipes).");
        }

        return sb.ToString();
    }

    private static string Wrap(RouteCommand command, ActResult result) =>
        ResultFormatter.Format(result) + $"\nrouted rule={command.Rule}";

    /// <summary>The reply for a request that was not run: nothing was sent, and the caller's own model takes over.</summary>
    internal static string NeedsBrain(string code, string detail)
    {
        var sb = new StringBuilder("needs_brain reason=").Append(code).Append(" input_sent=false retry=safe\n");
        sb.Append("detail: ").Append(ResultFormatter.OneLine(detail)).Append('\n');
        sb.Append("hint: handle this request yourself with the other tools (omniwin_guide topic=start).");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ name resolution (candidates come from OmniWin, never invented)

    internal static (WindowInfo? Window, NotRouted? Why) ResolveWindow(IReadOnlyList<WindowInfo> windows, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            var foreground = windows.FirstOrDefault(w => w.IsForeground);
            return foreground is null ? (null, new NotRouted("no_window", "No window is in the foreground.")) : (foreground, null);
        }

        var wanted = target.Trim();
        var named = windows.ToList();
        string Process(WindowInfo w) => w.Process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? w.Process[..^4] : w.Process;

        var exactProcess = named.Where(w => string.Equals(Process(w), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactProcess.Count == 1) return (exactProcess[0], null);
        if (exactProcess.Count > 1) return (null, new NotRouted("several_windows", $"{exactProcess.Count} windows of \"{wanted}\" are open; name one."));

        // A fragment shorter than three characters would match almost anything: it is never enough to pick a window.
        if (wanted.Length < 3) return (null, new NotRouted("no_window", $"\"{wanted}\" is too short to pick a window; name it."));
        var partial = named.Where(w => Process(w).Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                                       w.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return (partial[0], null);
        return partial.Count == 0
            ? (null, new NotRouted("no_window", $"No open window matches \"{wanted}\"."))
            : (null, new NotRouted("several_windows", $"{partial.Count} windows match \"{wanted}\"; name one."));
    }

    internal static InstalledApp? ResolveApp(IReadOnlyList<InstalledApp> apps, string name)
    {
        var exact = apps.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var starts = apps.Where(a => a.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return starts.Count == 1 ? starts[0] : null;
    }

    internal static WorkflowSummary? ResolveWorkflow(IReadOnlyList<WorkflowSummary> all, string name)
    {
        static string Key(string s) => s.Replace('_', ' ').Replace('-', ' ').Trim().ToLowerInvariant();
        var wanted = Key(name);
        var exact = all.Where(w => Key(w.Name) == wanted).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var starts = all.Where(w => Key(w.Name).StartsWith(wanted, StringComparison.Ordinal)).ToList();
        return starts.Count == 1 ? starts[0] : null;
    }
}
