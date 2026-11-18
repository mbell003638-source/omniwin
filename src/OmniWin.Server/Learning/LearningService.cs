using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Router;
using OmniWin.Workflows;

namespace OmniWin.Server.Learning;

/// <summary>A learned recipe that matches a request exactly, with the values for its slots.</summary>
internal sealed record RecipeMatch(RecipeState Recipe, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Contract 2.6: "do it once, then replay it". An <b>episode</b> is one task the AI is doing (it opens when omniwin_do answers
/// needs_brain). When the AI calls task_done, the acts recorded during the episode are saved as a recipe: an ordinary workflow
/// plus a card with the request wording. Replays append events; trust is computed from them. A replay that stops part-way opens
/// a repair episode, and the next task_done saves a new version. Nothing is learned silently, nothing is replaced or deleted,
/// and every step of every replay goes through the same checks and safety gate as a single tool call.
/// </summary>
internal sealed partial class LearningService : IComponentStatus
{
    public const int MaxWords = PhraseRouter.MaxWords;
    private const int MinSteps = 2;
    private const int ShortlistSize = 3;

    private readonly WorkflowService _workflows;
    private readonly IRunLog _log;
    private readonly RecipeStore _store;
    private readonly OmniWinConfig _config;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, RecipeTemplate?> _templates = new(StringComparer.Ordinal);
    private Episode? _episode;
    private string? _lastClosedMarker;

    public LearningService(WorkflowService workflows, IRunLog log, RecipeStore store, OmniWinConfig config, TimeProvider? time = null)
    {
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _time = time ?? TimeProvider.System;
    }

    private sealed class Episode
    {
        public required string Goal { get; init; }          // normalised
        public required long StartedAt { get; init; }       // Stopwatch-style timestamp from the time provider
        public string? Marker { get; set; }                 // id of the last run-log step before the episode's own steps
        public long LastActAt { get; set; }
        public bool Noisy { get; set; }
        public string? UsedRecipe { get; set; }             // a recipe was replayed cleanly inside this episode
        public IReadOnlyDictionary<string, string>? UsedValues { get; set; }
        public int Replays { get; set; }                    // clean recipe replays in this episode
        public int OtherActs { get; set; }                  // every act that was not such a replay (recorded or not)
        public string? RepairOf { get; set; }
        public int RepairStep { get; set; }
    }

    private LearningConfig Settings => _config.Learning;

    public RecipeStore Store => _store;

    // ------------------------------------------------------------------ omniwin_status

    public string Component => "learning";

    public string State => Settings.Enabled ? (Settings.AutoRun ? "on" : "on_no_auto_run") : "off";

    public string? Detail
    {
        get
        {
            var all = _store.All(Settings.PromoteAfter);
            var active = all.Where(r => r.Active).ToList();
            bool open;
            lock (_gate) open = Current() is not null;
            return string.Create(CultureInfo.InvariantCulture,
                $"{active.Count} recipes, {active.Count(r => r.Trusted)} trusted, {all.Sum(r => r.Runs)} replays, {all.Sum(r => r.Stops)} stopped; episode {(open ? "open" : "none")}");
        }
    }

    // ------------------------------------------------------------------ episodes

    private string? LastStepId()
    {
        var steps = _log.Steps;
        return steps.Count == 0 ? null : steps[^1].StepId;
    }

    /// <summary>The open episode, or null. An episode nobody touched for the idle limit is dropped. Caller holds the lock.</summary>
    private Episode? Current()
    {
        if (_episode is { } e && _time.GetElapsedTime(e.LastActAt) > TimeSpan.FromMinutes(Settings.EpisodeIdleMinutes))
        {
            Close(e);
        }

        return _episode;
    }

    private void Close(Episode episode)
    {
        _lastClosedMarker = LastStepId();
        if (ReferenceEquals(_episode, episode)) _episode = null;
        EpisodeClosed?.Invoke(episode.RepairOf is null ? episode.UsedRecipe : null);
    }

    /// <summary>A task ended. The argument is the saved recipe the AI ran for it, or null when it did the task some other way.</summary>
    public event Action<string?>? EpisodeClosed;

    /// <summary>The user said a recipe ran for the wrong request.</summary>
    public event Action<string>? WrongPick;

    /// <summary>omniwin_do answered needs_brain: the AI is about to do this task itself.</summary>
    public void OpenEpisode(string requestText)
    {
        if (!Settings.Enabled) return;
        var goal = RecipeTemplate.Normalize(requestText);
        if (goal.Length == 0) return;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_episode is { } unfinished) Close(unfinished);   // a new request: the earlier task ends unlearned
            _episode = new Episode { Goal = goal, StartedAt = now, LastActAt = now, Marker = LastStepId() };
        }
    }

    /// <summary>An acting tool call finished. <paramref name="recorded"/>: it went into the run log (so it can be replayed).</summary>
    public void NoteAct(bool recorded)
    {
        lock (_gate)
        {
            if (Current() is not { } episode) return;
            episode.LastActAt = _time.GetTimestamp();
            episode.OtherActs++;
            if (!recorded) episode.Noisy = true;
        }
    }

    /// <summary>
    /// A tool outside the run log sent input during the task (an engine call, a selection replace, a quick command, a power tool).
    /// It cannot be replayed, so a recipe saved from this task would be missing that step: the task is noisy (review 27, L3).
    /// </summary>
    public void NoteUntrackedAct() => NoteAct(recorded: false);

    // ------------------------------------------------------------------ task_done

    public string TaskDone(bool success, string? goal, string? template, IReadOnlyDictionary<string, string>? renames)
    {
        if (!Settings.Enabled) return Refuse("learning_off", "Learning is switched off in config.json (learning.enabled).");
        lock (_gate)
        {
            var episode = Current();
            if (!success)
            {
                if (episode is not null) Close(episode);
                else _lastClosedMarker = LastStepId();
                return "task_done status=confirmed input_sent=false retry=done learned=false\ndetail: Nothing was learned (success=false).";
            }

            var goalText = episode?.Goal ?? RecipeTemplate.Normalize(goal ?? string.Empty);
            if (goalText.Length == 0)
            {
                return Refuse("no_episode", "No task is open. Call omniwin_do with the user's request first, or pass goal with the request wording.");
            }

            if (episode is { UsedRecipe: { } used, RepairOf: null })
            {
                // Review 27, L1: a wording is learned only here, on task_done(success=true), and only when the saved recipe was the
                // whole task. If the AI did anything else in this task, the request means more than the recipe does.
                var whole = episode.Replays == 1 && episode.OtherActs == 0;
                var wording = whole && TryLearnWording(used, episode.Goal, episode.UsedValues);
                Close(episode);
                var detail = !whole
                    ? "The saved recipe was only part of this task (other steps were done as well), so nothing was learned: neither a new wording for the recipe nor the extra steps."
                    : wording ? "The task ran from the saved recipe. This wording was added to it, so the same sentence can run it next time."
                    : "The task ran from the saved recipe; there is nothing new to learn.";
                return $"task_done status=confirmed input_sent=false retry=done learned=false recipe={used}\ndetail: {detail}";
            }

            if (PhraseRouter.HasRiskyWord(goalText))
            {
                return Refuse("risky_words", "The request has a word (delete, install, send, pay and similar) that is never learned for replay.");
            }

            var marker = episode is not null ? episode.Marker : _lastClosedMarker;
            var steps = StepsAfter(marker);
            return episode is { RepairOf: { } old }
                ? Repair(episode, old, steps, renames)
                : Learn(episode, goalText, steps, template, renames);
        }
    }

    private List<RunStep> StepsAfter(string? marker)
    {
        var all = _log.Steps;
        if (marker is null) return [.. all];
        var index = -1;
        for (var i = all.Count - 1; i >= 0; i--)
        {
            if (string.Equals(all[i].StepId, marker, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        // A marker that fell out of the log (2,000 steps later) means the start of the task is gone too: learn nothing from a part.
        return index < 0 ? [] : [.. all.Skip(index + 1)];
    }

    private string Learn(Episode? episode, string goal, List<RunStep> steps, string? templateText, IReadOnlyDictionary<string, string>? renames)
    {
        if (steps.Count < MinSteps)
        {
            return Refuse("too_few_steps", $"Only {steps.Count} recorded step(s) since the task started; a recipe needs at least {MinSteps}. (Acts are recorded when they end confirmed, or unverified with a visible change.)");
        }

        if (steps.Count > Settings.EpisodeMaxSteps)
        {
            return Refuse("too_many_steps", $"{steps.Count} steps were recorded; a learned recipe has at most {Settings.EpisodeMaxSteps}.");
        }

        if (IsBuiltInCommand(goal))
        {
            return Refuse("template_conflict", "This request is already a built-in quick command of omniwin_do; there is nothing to learn.");
        }

        if (_store.All(Settings.PromoteAfter).Count(r => r.Active) >= Settings.MaxRecipes)
        {
            return Refuse("too_many_recipes", $"There are already {Settings.MaxRecipes} recipes (learning.max_recipes). Disable some with workflow_manage.");
        }

        var placeholders = steps.Where(s => s.ValueParam is not null).Select(s => s.ValueParam!).ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (renames is not null)
        {
            foreach (var (placeholder, slot) in renames)
            {
                if (!placeholders.Contains(placeholder, StringComparer.Ordinal))
                {
                    return Refuse("template_mismatch", $"params names \"{placeholder}\", which is not a text placeholder of this task. Placeholders: {(placeholders.Count == 0 ? "none" : string.Join(", ", placeholders))}.");
                }

                if (!WorkflowStore.IsValidParamName(slot) || map.Values.Contains(slot, StringComparer.Ordinal))
                {
                    return Refuse("template_mismatch", $"\"{slot}\" is not usable as a slot name (lower case letter first, then lower case letters, digits or '_'; each used once).");
                }

                map[placeholder] = slot;
            }
        }

        RecipeTemplate? template = null;
        if (!string.IsNullOrWhiteSpace(templateText))
        {
            template = RecipeTemplate.Parse(templateText, out var problem);
            if (template is null) return Refuse("template_mismatch", problem!);
            if (template.Match(goal) is null)
            {
                return Refuse("template_mismatch", $"The template does not match the request \"{goal}\". Write the request again with each changing part as {{slot}}.");
            }

            var unknown = template.Slots.Where(s => !map.Values.Contains(s, StringComparer.Ordinal)).ToList();
            if (unknown.Count > 0)
            {
                return Refuse("template_mismatch", $"The template has slot(s) {string.Join(", ", unknown)} that params does not map to a placeholder. Placeholders: {(placeholders.Count == 0 ? "none" : string.Join(", ", placeholders))}.");
            }
        }
        else if (placeholders.Count == 0)
        {
            template = RecipeTemplate.Parse(goal, out _);   // no changing parts: the wording itself is the template (null if it is too short)
        }

        var name = FreeName(Slug(goal));
        var saved = _workflows.SaveLearned(name, "learned: " + goal, [], Generalize(steps), map);
        if (saved.Status != SaveStatus.Saved)
        {
            return saved.Status == SaveStatus.Denied
                ? Refuse(ReasonCodes.PolicyForbidden, saved.Detail ?? "A step would be denied by the safety gate.", denied: true)
                : Refuse(saved.Reason ?? ReasonCodes.UnsupportedAction, saved.Detail ?? "The recipe could not be saved.");
        }

        var card = WriteCard(name, goal, template?.Text, [], saved, episode, supersedes: null);
        _store.Append(name, RecipeEventKinds.Learned);

        // The newest recipe for a wording wins: an older one with the same wording would make every match ambiguous.
        var notes = new List<string>();
        if (template is not null)
        {
            foreach (var older in _store.All(Settings.PromoteAfter).Where(r => r.Active && r.Name != name && r.Templates.Contains(template.Text, StringComparer.Ordinal)))
            {
                _store.Append(older.Name, RecipeEventKinds.Superseded, by: name);
                notes.Add($"note: replaces the older recipe {older.Name} for the same wording.");
            }
        }

        if (episode is not null) Close(episode);
        else _lastClosedMarker = LastStepId();
        return Learned(card, saved, template, steps, map, notes, episode?.Noisy == true);
    }

    private string Repair(Episode episode, string oldName, List<RunStep> steps, IReadOnlyDictionary<string, string>? renames)
    {
        var (loaded, old) = _workflows.Store.Load(oldName);
        var oldState = _store.Get(oldName, Settings.PromoteAfter);
        if (loaded.Status != StoreStatus.Ok || old is null || oldState is null)
        {
            Close(episode);
            return Refuse(ReasonCodes.NotFound, $"The recipe {oldName} that stopped could not be read again; nothing was repaired.");
        }

        if (steps.Count == 0)
        {
            return Refuse("too_few_steps", "No act was recorded after the replay stopped, so there is nothing to repair the recipe with.");
        }

        var prefix = old.Steps.Take(Math.Max(0, episode.RepairStep - 1)).ToList();
        if (prefix.Count + steps.Count > Settings.EpisodeMaxSteps)
        {
            return Refuse("too_many_steps", $"The repaired recipe would have {prefix.Count + steps.Count} steps; the limit is {Settings.EpisodeMaxSteps}.");
        }

        var name = NextVersion(oldName);
        var saved = _workflows.SaveLearned(name, old.Description, prefix, Generalize(steps), renames);
        if (saved.Status != SaveStatus.Saved)
        {
            return saved.Status == SaveStatus.Denied
                ? Refuse(ReasonCodes.PolicyForbidden, saved.Detail ?? "A step would be denied by the safety gate.", denied: true)
                : Refuse(saved.Reason ?? ReasonCodes.UnsupportedAction, saved.Detail ?? "The repaired recipe could not be saved.");
        }

        // Wordings carry over only while every slot still has a parameter in the repaired steps.
        var kept = oldState.Templates.Where(t => Template(t) is { } tpl && SameSet(tpl.Slots, saved.ParamNames)).ToList();
        var main = oldState.Card.Template is { } m && kept.Contains(m, StringComparer.Ordinal) ? m : kept.FirstOrDefault();
        var card = WriteCard(name, oldState.Card.Goal, main, [.. kept.Where(t => t != main)], saved, episode, supersedes: oldName);
        _store.Append(oldName, RecipeEventKinds.Superseded, by: name);
        _store.Append(name, RecipeEventKinds.Repaired, step: episode.RepairStep);
        Close(episode);
        var notes = new List<string> { $"note: repaired from step {episode.RepairStep} of {oldName}; the old version is kept on disk and no longer used." };
        if (main is null && saved.ParamNames.Count > 0) notes.Add("note: no saved wording fits the new parameters, so the AI runs it with workflow_run until a wording is learned.");
        return Learned(card, saved, main is null ? null : Template(main), steps, renames ?? new Dictionary<string, string>(), notes, noisy: false);
    }

    private RecipeCard WriteCard(string name, string goal, string? template, IReadOnlyList<string> aliases, SaveOutcome saved, Episode? episode, string? supersedes)
    {
        var (_, workflow) = _workflows.Store.Load(name);
        var steps = workflow?.Steps ?? [];
        var eligible = steps.Count > 0 && steps.All(s => s.Engine != StepTools.Browser && _workflows.ClassifyStep(s) is RiskClass.Read or RiskClass.Normal);
        var wall = episode is null ? 0 : _time.GetElapsedTime(episode.StartedAt).TotalMilliseconds;
        var card = new RecipeCard(RecipeStore.CardVersion, name, goal, template, aliases, [.. saved.ParamNames], saved.StepCount, Math.Round(wall, 1),
            [.. steps.Select(s => s.Tool).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. steps.Select(s => s.Target?.Process).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            eligible && episode?.Noisy != true, episode?.Noisy == true, supersedes, _store.Now);
        _store.SaveCard(card);
        return card;
    }

    private string Learned(RecipeCard card, SaveOutcome saved, RecipeTemplate? template, IReadOnlyList<RunStep> newSteps,
        IReadOnlyDictionary<string, string> map, List<string> notes, bool noisy)
    {
        var inv = CultureInfo.InvariantCulture;
        var runnable = card.AutoRunEligible && template is not null && SameSet(template.Slots, saved.ParamNames);
        var sb = new StringBuilder(256);
        sb.Append("task_done status=confirmed input_sent=false retry=done learned=true recipe=").Append(card.Workflow)
          .Append(" steps=").Append(saved.StepCount.ToString(inv)).Append(" trust=candidate auto_run=").Append(runnable ? "after_" + Settings.PromoteAfter.ToString(inv) + "_clean_replays" : "no");
        if (template is not null) sb.Append('\n').Append("template: ").Append(template.Text);
        for (var i = 0; i < newSteps.Count; i++)
        {
            var step = newSteps[i];
            if (step.ValueParam is null) continue;
            var param = map.TryGetValue(step.ValueParam, out var renamed) ? renamed : step.ValueParam;
            sb.Append('\n').Append("slot ").Append(param).Append(" -> ").Append(step.Tool).Append(' ').Append(ResultFormatter.OneLine(step.Action))
              .Append(" on ").Append(ResultFormatter.Quote(step.Target?.Name ?? step.Target?.Role ?? "(no element)", 60));
        }

        foreach (var note in notes) sb.Append('\n').Append(note);
        if (noisy) sb.Append('\n').Append("note: an act failed, was not recorded, or was done by a tool that cannot be replayed during this task, so a step may be missing. This recipe never runs by itself; run it with workflow_run and check the result.");
        else if (!card.AutoRunEligible) sb.Append('\n').Append("note: it has a browser step or a risky step, so it never runs by itself; run it with workflow_run.");
        else if (!runnable) sb.Append('\n').Append("note: no wording with a slot for every text parameter was given, so it cannot run by itself yet; run it with workflow_run and its params.");
        sb.Append('\n').Append("hint: next time, if omniwin_do lists this recipe, run it with workflow_run(name, params).");
        return sb.ToString();
    }

    private static string Refuse(string reason, string detail, bool denied = false) =>
        $"task_done status={(denied ? "denied" : "blocked")} reason={reason} input_sent=false retry=no learned=false\ndetail: {ResultFormatter.OneLine(detail)}";

    /// <summary>
    /// True when the phrase table already runs this request by itself (pause, volume 30, mute). A rule that takes a free name
    /// ("open X", "switch to X", "run workflow X") does not count: "open invoice 42 in word" reads as "open an app" to the table,
    /// fails there, and is exactly the kind of task that is worth learning.
    /// </summary>
    internal static bool IsBuiltInCommand(string goal) =>
        PhraseRouter.Parse(goal).Command is { } command && command is not (LaunchCommand or WorkflowCommand or WindowCommand { Target: not null });

    /// <summary>
    /// A window title usually starts with the document ("invoice 42 - Word"). A recipe must find the same app with another document
    /// open, so the part before the last " - " becomes a wildcard and the app part stays: "* - Word". A title without that shape (a
    /// dialog such as "Save As") is kept exactly. The safety gate still sees the real window when each step runs.
    /// </summary>
    internal static string GeneralizeTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return title;
        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var cut = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (cut > 0 && cut + separator.Length < title.Length) return "*" + title[cut..];
        }

        return title;
    }

    private static List<RunStep> Generalize(IReadOnlyList<RunStep> steps) =>
        [.. steps.Select(s => s.Target is { Engine: StepTools.Desktop } t ? s with { Target = t with { WindowTitle = GeneralizeTitle(t.WindowTitle) } } : s)];

    // ------------------------------------------------------------------ names

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    [GeneratedRegex(@"-v(\d+)$")]
    private static partial Regex VersionSuffix();

    internal static string Slug(string goal)
    {
        var slug = NotSlug().Replace(goal.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "recipe" : slug;
    }

    private bool Taken(string name) =>
        _workflows.Store.Load(name).Result.Status != StoreStatus.NotFound || _store.LoadCard(name) is not null;

    private string FreeName(string slug)
    {
        if (!Taken(slug)) return slug;
        for (var n = 2; ; n++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{slug}-{n}");
            if (!Taken(candidate)) return candidate;
        }
    }

    private string NextVersion(string oldName)
    {
        var match = VersionSuffix().Match(oldName);
        var stem = match.Success ? oldName[..match.Index] : oldName;
        var n = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + 1 : 2;
        if (stem.Length > 56) stem = stem[..56].TrimEnd('-');
        for (; ; n++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem}-v{n}");
            if (!Taken(candidate)) return candidate;
        }
    }

    // ------------------------------------------------------------------ matching

    private RecipeTemplate? Template(string text) => _templates.GetOrAdd(text, static t => RecipeTemplate.Parse(t, out _));

    private static bool SameSet(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b) =>
        a.Count == b.Count && a.All(x => b.Contains(x, StringComparer.Ordinal));

    /// <summary>True when this recipe may be run without the AI: trusted, low-risk, desktop-only.</summary>
    internal static bool MayAutoRun(RecipeState recipe) => recipe.Active && recipe.Trusted && recipe.Card.AutoRunEligible;

    /// <summary>
    /// Speed 1: the one trusted recipe whose wording matches this request as a whole sentence. Null when none or more than one
    /// does (<paramref name="ambiguous"/> says which), or when the request is long or has a risky word.
    /// </summary>
    public RecipeMatch? FindExact(string requestText, out bool ambiguous)
    {
        ambiguous = false;
        if (!Settings.Enabled || !Settings.AutoRun || string.IsNullOrEmpty(requestText)) return null;

        // One plain sentence only: a request with a line break is two requests, and braces are template syntax, not a value.
        if (requestText.AsSpan().IndexOfAny("{}\r\n\t") >= 0) return null;
        var normalized = RecipeTemplate.Normalize(requestText);
        if (normalized.Length == 0 || PhraseRouter.HasRiskyWord(normalized)) return null;
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxWords) return null;

        var matches = new List<RecipeMatch>();
        foreach (var recipe in _store.All(Settings.PromoteAfter))
        {
            if (!MayAutoRun(recipe)) continue;
            foreach (var text in recipe.Templates)
            {
                if (Template(text) is not { } template || !SameSet(template.Slots, recipe.Card.Params)) continue;
                if (template.Match(normalized, requestText) is { } values)
                {
                    matches.Add(new RecipeMatch(recipe, values));
                    break;
                }
            }
        }

        if (matches.Count == 1) return matches[0];
        ambiguous = matches.Count > 1;
        return null;
    }

    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "to", "in", "on", "of", "for", "and", "my", "me", "it", "this", "that", "with", "from", "is", "at",
    };

    private static HashSet<string> Words(string normalized)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SmallWords.Contains(word) && !word.StartsWith('{')) set.Add(word);
        }

        return set;
    }

    /// <summary>Speed 2: the recipes most like this request by shared words (no model), for the AI to pick from.</summary>
    public IReadOnlyList<RecipeState> Shortlist(string requestText)
    {
        if (!Settings.Enabled) return [];
        var wanted = Words(RecipeTemplate.Normalize(requestText));
        if (wanted.Count == 0) return [];
        var scored = new List<(RecipeState Recipe, double Score)>();
        foreach (var recipe in _store.All(Settings.PromoteAfter))
        {
            if (!recipe.Active) continue;
            var have = Words(recipe.Card.Goal);
            foreach (var template in recipe.Templates) have.UnionWith(Words(template));
            var shared = wanted.Count(have.Contains);
            if (shared == 0) continue;
            var score = shared / (double)(wanted.Count + have.Count - shared);
            if (score >= 0.1) scored.Add((recipe, score));
        }

        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Recipe.Name, StringComparer.Ordinal).Take(ShortlistSize).Select(s => s.Recipe)];
    }

    /// <summary>One line the AI can act on: <c>recipe: name(params) steps=N trust=… goal="…"</c>.</summary>
    public static string ShortlistLine(RecipeState recipe) =>
        string.Create(CultureInfo.InvariantCulture,
            $"recipe: {recipe.Name}({string.Join(",", recipe.Card.Params)}) steps={recipe.Card.Steps} trust={recipe.Trust} goal={ResultFormatter.Quote(recipe.Card.Goal, 80)}");

    // ------------------------------------------------------------------ replay

    /// <summary>
    /// Runs a saved workflow and, when it is a learned recipe, keeps the books: a clean run or a stop is appended as an event, a stop
    /// part-way opens a repair episode, and a clean run the AI started for a differently worded request may teach that wording.
    /// <paramref name="requestText"/> is the user's wording when omniwin_do started the run itself.
    /// </summary>
    public async Task<RunOutcome> RunAsync(string name, IReadOnlyDictionary<string, string>? values, string? requestText, CancellationToken ct)
    {
        var card = _store.LoadCard(name);
        var watch = Stopwatch.StartNew();
        var outcome = await _workflows.RunWorkflowAsync(name, values, ct).ConfigureAwait(false);
        if (card is null || !Settings.Enabled || outcome.Total == 0) return outcome;

        var clean = outcome.StoppedAt == 0 && outcome.Status is ActStatus.Confirmed or ActStatus.Unverified;
        if (clean)
        {
            _store.Append(name, RecipeEventKinds.ReplayOk, wallMs: watch.Elapsed.TotalMilliseconds);
            lock (_gate)
            {
                if (requestText is null && Current() is { RepairOf: null } episode)
                {
                    episode.UsedRecipe = name;
                    episode.UsedValues = values;
                    episode.Replays++;
                    episode.LastActAt = _time.GetTimestamp();
                }
            }

            return outcome;
        }

        // The stop key, a timeout or a cancel says nothing about the recipe. Nor does a first step that found nothing and sent
        // nothing: the app is simply not there yet.
        var interrupted = outcome.Status == ActStatus.Denied || outcome.Reason is ReasonCodes.KillSwitch or ReasonCodes.InputInterrupted or ReasonCodes.EngineTimeout;
        if (interrupted || outcome.StoppedAt <= 0 || (outcome.StoppedAt == 1 && !outcome.InputSent)) return outcome;

        _store.Append(name, RecipeEventKinds.ReplayStopped, step: outcome.StoppedAt, wallMs: watch.Elapsed.TotalMilliseconds);
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var goal = requestText is not null ? RecipeTemplate.Normalize(requestText) : Current()?.Goal ?? card.Goal;
            _episode = new Episode
            {
                Goal = goal, StartedAt = now, LastActAt = now, Marker = LastStepId(), RepairOf = name, RepairStep = outcome.StoppedAt,
            };
        }

        return outcome;
    }

    /// <summary>The AI ran this recipe for a request that matched no saved wording: remember the wording, with the values as slots.</summary>
    private bool TryLearnWording(string name, string goal, IReadOnlyDictionary<string, string>? values)
    {
        if (_store.Get(name, Settings.PromoteAfter) is not { Active: true, Card.AutoRunEligible: true } recipe) return false;
        if (PhraseRouter.HasRiskyWord(goal) || IsBuiltInCommand(goal)) return false;
        var aliases = recipe.Templates.Count - (recipe.Card.Template is null ? 0 : 1);
        if (aliases >= RecipeStore.MaxAliases) return false;

        var wording = " " + goal + " ";
        foreach (var param in recipe.Card.Params)
        {
            if (values is null || !values.TryGetValue(param, out var raw)) return false;
            var value = RecipeTemplate.Normalize(raw);
            if (value.Length == 0) return false;
            var needle = " " + value + " ";
            var first = wording.IndexOf(needle, StringComparison.Ordinal);
            if (first < 0 || wording.IndexOf(needle, first + 1, StringComparison.Ordinal) >= 0) return false;   // must appear exactly once
            wording = wording[..first] + " {" + param + "} " + wording[(first + needle.Length)..];
        }

        if (RecipeTemplate.Parse(wording, out _) is not { } template || !SameSet(template.Slots, recipe.Card.Params)) return false;
        if (template.Match(goal) is null || recipe.Templates.Contains(template.Text, StringComparer.Ordinal)) return false;
        _store.Append(name, RecipeEventKinds.AliasAdded, alias: template.Text);
        return true;
    }

    // ------------------------------------------------------------------ workflow_manage

    public string Manage(string name, string action)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (verb is not ("disable" or "enable" or "right" or "wrong_pick"))
        {
            return $"workflow_manage status=blocked reason={ReasonCodes.UnsupportedAction} input_sent=false retry=no\ndetail: action is disable, enable, right or wrong_pick.";
        }

        if (!WorkflowStore.IsValidName(name) || _store.Get(name, Settings.PromoteAfter) is null)
        {
            return $"workflow_manage status=blocked reason={ReasonCodes.NotFound} input_sent=false retry=no\ndetail: No learned recipe has that name (workflow_list shows them).";
        }

        var written = _store.Append(name, verb switch
        {
            "disable" => RecipeEventKinds.Disabled,
            "enable" => RecipeEventKinds.Enabled,
            "right" => RecipeEventKinds.Right,
            _ => RecipeEventKinds.WrongPick,
        });
        if (!written)
        {
            // A lost "disable" or "wrong_pick" would leave a recipe trusted while the caller believes it is not.
            return $"workflow_manage name={name} status=error reason={ReasonCodes.EngineTimeout} input_sent=false retry=safe action={verb}\n" +
                   "detail: The note could NOT be written (the recipes file is busy or cannot be written). Nothing changed: the recipe is as it was. Try again.";
        }

        if (verb == "wrong_pick") WrongPick?.Invoke(name);
        var now = _store.Get(name, Settings.PromoteAfter)!;
        return $"workflow_manage name={name} status=confirmed input_sent=false retry=done action={verb} trust={now.Trust}\ndetail: Recorded. Nothing is ever deleted; a disabled recipe is only no longer offered or run by itself.";
    }

    /// <summary>The learned side of workflow_list: one line per recipe.</summary>
    public IReadOnlyList<string> ListLines()
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        foreach (var r in _store.All(Settings.PromoteAfter))
        {
            if (r.Superseded) continue;
            var line = new StringBuilder();
            line.Append("learned ").Append(r.Name).Append(" trust=").Append(r.Trust).Append(" runs=").Append(r.Runs.ToString(inv))
                .Append(" stops=").Append(r.Stops.ToString(inv)).Append(" last=").Append(r.Last ?? "never");
            if (r.MedianReplayMs is { } median && r.Card.LearnedWallMs > 0)
            {
                line.Append(" first_run=").Append(ResultFormatter.FormatMs(r.Card.LearnedWallMs)).Append("ms replay=").Append(ResultFormatter.FormatMs(median)).Append("ms");
            }

            if (r.Templates.Count > 0) line.Append(" wording=").Append(ResultFormatter.Quote(r.Templates[0], 80));
            lines.Add(line.ToString());
        }

        return lines;
    }
}
