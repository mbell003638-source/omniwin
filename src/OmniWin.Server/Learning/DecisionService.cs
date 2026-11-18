using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Router;

namespace OmniWin.Server.Learning;

/// <summary>One thing the decision model may pick. Built by OmniWin: a model never sees anything else and never invents an option.</summary>
internal sealed record DecisionOption(string Id, string Label, IReadOnlyList<string> Examples);

internal sealed record DecisionPick(string Id, double Confidence);

internal sealed record DecisionAnswer(IReadOnlyList<DecisionPick> Ranked);

/// <summary>
/// A "picker" model (contract 2.6): the request and a list of options in, a confidence per option out. It writes no text, plans
/// nothing and supplies no value. <see cref="IsRemote"/> says whether the request wording leaves this PC.
/// </summary>
internal interface IDecisionModel
{
    string Name { get; }

    bool IsRemote { get; }

    Task<DecisionAnswer?> RankAsync(string request, IReadOnlyList<DecisionOption> options, CancellationToken ct);
}

/// <summary>What the decision stage concluded. Exactly one of <see cref="Recipe"/> / <see cref="Command"/> is set when it may act.</summary>
internal sealed record Decision(RecipeMatch? Recipe, RouteCommand? Command, string? Reason, string? Detail, IReadOnlyList<string> Suggested)
{
    public static readonly Decision NotAsked = new(null, null, null, null, []);

    public bool Acts => Recipe is not null || Command is not null;
}

/// <summary>
/// The optional decision stage of omniwin_do. Off unless the owner wires a model: with <c>decision.provider = "none"</c> nothing is
/// constructed or called. <b>shadow</b>: the model is asked and its confident picks are scored against what really happened, but
/// nothing is run from them. <b>act</b>: a pick is run only when it clears every rule (confidence, lead over the second pick, speed,
/// no risky word, a trusted low-risk recipe or a built-in command, every value found word for word in the request); anything else
/// goes to the AI. A slow, failing or unreachable model never fails a request. OmniWin never switches shadow to act by itself, and a
/// reported wrong pick forces shadow until the owner edits config.json.
/// </summary>
internal sealed class DecisionService : IComponentStatus
{
    public const string RecipePrefix = "recipe:";
    public const string IntentPrefix = "intent:";
    public const string NoneId = "none";

    private readonly OmniWinConfig _config;
    private readonly LearningService _learning;
    private readonly IDecisionModel? _model;
    private readonly string _eventsFile;
    private readonly Func<DateTimeOffset?> _configChangedAt;
    private readonly object _gate = new();
    private (string Id, double Confidence, double LatencyMs)? _pending;   // a confident shadow pick waiting for the task to end
    private string? _lastActedRecipe;

    public DecisionService(OmniWinConfig config, LearningService learning, IDecisionModel? model, string recipesDir, Func<DateTimeOffset?>? configChangedAt = null)
    {
        _config = config;
        _learning = learning;
        _model = model;
        _eventsFile = Path.Combine(recipesDir, "decision-events.jsonl");
        _configChangedAt = configChangedAt ?? (() => null);
        learning.EpisodeClosed += OnEpisodeClosed;
        learning.WrongPick += OnWrongPick;
    }

    private DecisionConfig Settings => _config.Decision;

    private sealed record Line(DateTimeOffset At, string Kind, string? Pick = null, double? Confidence = null, double? LatencyMs = null, bool? Right = null);

    private static readonly JsonSerializerOptions LineOptions = new(CoreJson.Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ------------------------------------------------------------------ state

    /// <summary>Why the model cannot be used at all, or null.</summary>
    private string? Unusable =>
        Settings.Provider == DecisionConfig.ProviderNone || Settings.Mode == DecisionConfig.ModeOff ? "off"
        : _model is null ? $"provider \"{Settings.Provider}\" has no adapter in this build"
        : _model.IsRemote && !Settings.AllowRemote ? "the model is not on this PC and decision.allow_remote is false"
        : null;

    /// <summary>True when a wrong pick was reported after the last edit of config.json: the model only watches until the owner looks.</summary>
    private bool ForcedShadow
    {
        get
        {
            var forced = Lines().LastOrDefault(l => l.Kind == "forced_shadow");
            if (forced is null) return false;
            return _configChangedAt() is not { } edited || edited <= forced.At;
        }
    }

    public string Mode => Unusable is not null ? DecisionConfig.ModeOff
        : Settings.Mode == DecisionConfig.ModeAct && !ForcedShadow ? DecisionConfig.ModeAct : DecisionConfig.ModeShadow;

    public string Component => "decision";

    public string State => Unusable is { } why ? (why == "off" ? "off" : "unavailable") : Mode == DecisionConfig.ModeAct ? "act" : ForcedShadow ? "shadow_forced" : "shadow";

    public string? Detail
    {
        get
        {
            if (Unusable is { } why) return why == "off" ? "no decision model is wired (decision.provider is \"none\" or mode is \"off\")" : why;
            var judged = Lines().Where(l => l.Kind == "shadow" && l.Right is not null).ToList();
            var latencies = Lines().Where(l => l.LatencyMs is not null).Select(l => l.LatencyMs!.Value).Order().ToList();
            var median = latencies.Count == 0 ? "-" : latencies[latencies.Count / 2].ToString("0", CultureInfo.InvariantCulture) + " ms";
            return string.Create(CultureInfo.InvariantCulture,
                $"{_model!.Name}{(_model.IsRemote ? " (remote)" : string.Empty)}: {judged.Count} confident shadow picks judged, {judged.Count(l => l.Right == false)} wrong, {Lines().Count(l => l.Kind == "acted")} acted, median {median}");
        }
    }

    // ------------------------------------------------------------------ deciding

    private List<DecisionOption> Options(out Dictionary<string, RecipeState> recipes)
    {
        recipes = new Dictionary<string, RecipeState>(StringComparer.Ordinal);
        var options = new List<DecisionOption>();
        foreach (var recipe in _learning.Store.All(_config.Learning.PromoteAfter))
        {
            if (!LearningService.MayAutoRun(recipe)) continue;
            recipes[recipe.Name] = recipe;
            options.Add(new DecisionOption(RecipePrefix + recipe.Name, recipe.Card.Goal, recipe.Templates));
        }

        foreach (var intent in PhraseRouter.Intents)
        {
            if (intent != "none") options.Add(new DecisionOption(IntentPrefix + intent, intent.Replace('_', ' '), []));
        }

        options.Add(new DecisionOption(NoneId, "none of these: a new or complicated request", []));
        return options;
    }

    /// <summary>
    /// Called for a request that matched neither the phrase table nor a learned wording. Returns what may be run (act mode, every
    /// rule passed), or why the AI gets it, with the model's best recipes as suggestions.
    /// </summary>
    public async Task<Decision> DecideAsync(string text, CancellationToken ct)
    {
        if (Unusable is not null) return Decision.NotAsked;
        var normalized = RecipeTemplate.Normalize(text);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > Settings.MaxWords || PhraseRouter.HasRiskyWord(normalized) || text.AsSpan().IndexOfAny("{}\r\n\t") >= 0)
        {
            return Decision.NotAsked;
        }

        var options = Options(out var recipes);
        var watch = Stopwatch.StartNew();
        DecisionAnswer? answer;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Settings.TimeoutMs);
            answer = await _model!.RankAsync(normalized, options, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Brain("model_unavailable", "The decision model did not answer in time.", []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Brain("model_unavailable", "The decision model failed (" + ex.GetType().Name + ").", []);
        }

        var latency = watch.Elapsed.TotalMilliseconds;
        var known = options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var ranked = answer?.Ranked.Where(p => known.Contains(p.Id) && p.Confidence is >= 0 and <= 1).OrderByDescending(p => p.Confidence).ToList() ?? [];
        if (ranked.Count == 0) return Brain("model_unavailable", "The decision model gave no usable answer.", []);

        var top = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Confidence : 0;
        var suggested = ranked.Where(p => p.Id.StartsWith(RecipePrefix, StringComparison.Ordinal)).Take(3).Select(p => p.Id[RecipePrefix.Length..]).ToList();
        var confident = top.Confidence >= Settings.Threshold && top.Confidence - second >= Settings.Margin && latency <= Settings.MaxLatencyMs;

        if (Mode != DecisionConfig.ModeAct)
        {
            lock (_gate) _pending = confident ? (top.Id, top.Confidence, latency) : null;
            if (!confident) Append(new Line(DateTimeOffset.UtcNow, "shadow", LatencyMs: Math.Round(latency, 1)));
            return new Decision(null, null, null, null, suggested);   // shadow: the AI works as if no model were there
        }

        if (!confident)
        {
            Append(new Line(DateTimeOffset.UtcNow, "unsure", LatencyMs: Math.Round(latency, 1)));
            return Brain("low_confidence", string.Create(CultureInfo.InvariantCulture,
                $"The decision model was not sure enough or not fast enough (top pick at {top.Confidence:0.00}, lead {top.Confidence - second:0.00}, {latency:0} ms)."), suggested);
        }

        if (top.Id == NoneId) return Brain("no_rule", "The decision model says this is not a learned task or a quick command.", suggested);

        if (top.Id.StartsWith(IntentPrefix, StringComparison.Ordinal))
        {
            var intent = top.Id[IntentPrefix.Length..];
            if (PhraseRouter.FromIntent(intent, text) is not { } command)
            {
                return Brain("no_slots", $"The decision model chose {intent}, but its number or name could not be read from the request.", suggested);
            }

            Append(new Line(DateTimeOffset.UtcNow, "acted", top.Id, Math.Round(top.Confidence, 3), Math.Round(latency, 1)));
            return new Decision(null, command with { Rule = "decision." + intent }, null, null, suggested);
        }

        var recipe = recipes[top.Id[RecipePrefix.Length..]];
        if (Values(recipe, normalized, text) is not { } values)
        {
            return Brain("no_slots", $"The decision model chose the recipe {recipe.Name}, but its value(s) could not be found word for word in the request.", suggested);
        }

        lock (_gate) _lastActedRecipe = recipe.Name;
        Append(new Line(DateTimeOffset.UtcNow, "acted", top.Id, Math.Round(top.Confidence, 3), Math.Round(latency, 1)));
        return new Decision(new RecipeMatch(recipe, values), null, null, null, suggested);
    }

    private static Decision Brain(string reason, string detail, IReadOnlyList<string> suggested) => new(null, null, reason, detail, suggested);

    /// <summary>
    /// The value for a recipe's one text parameter: the single run of request words that are in none of the recipe's saved wordings.
    /// Two such runs, or more than one parameter, is not read at all (the AI is asked); the model never supplies a value.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? Values(RecipeState recipe, string normalized, string original)
    {
        if (recipe.Card.Params.Count == 0) return new Dictionary<string, string>();
        if (recipe.Card.Params.Count != 1) return null;

        var literal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in recipe.Templates)
        {
            foreach (var word in template.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!word.StartsWith('{')) literal.Add(word);
            }
        }

        if (literal.Count == 0) return null;
        var runs = new List<string>();
        var current = new List<string>();
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (literal.Contains(word))
            {
                if (current.Count > 0) runs.Add(string.Join(' ', current));
                current.Clear();
            }
            else
            {
                current.Add(word);
            }
        }

        if (current.Count > 0) runs.Add(string.Join(' ', current));
        if (runs.Count != 1 || !RecipeTemplate.IsValue(runs[0])) return null;

        var value = runs[0];
        var first = original.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        if (first >= 0 && original.IndexOf(value, first + 1, StringComparison.OrdinalIgnoreCase) < 0) value = original.Substring(first, value.Length);
        return new Dictionary<string, string>(StringComparer.Ordinal) { [recipe.Card.Params[0]] = value };
    }

    // ------------------------------------------------------------------ scoring and the wrong-pick brake

    private void OnEpisodeClosed(string? usedRecipe)
    {
        (string Id, double Confidence, double LatencyMs)? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is not { } pick) return;

        // What really happened: the AI ran a saved recipe, or did something else. A picked built-in command cannot be judged
        // this way (the AI may have used any tool for it), so it is counted without a verdict.
        bool? right = pick.Id.StartsWith(IntentPrefix, StringComparison.Ordinal) ? null
            : pick.Id == NoneId ? usedRecipe is null
            : string.Equals(pick.Id, RecipePrefix + usedRecipe, StringComparison.Ordinal);
        Append(new Line(DateTimeOffset.UtcNow, "shadow", pick.Id, Math.Round(pick.Confidence, 3), Math.Round(pick.LatencyMs, 1), right));
    }

    private void OnWrongPick(string recipe)
    {
        bool mine;
        lock (_gate) mine = string.Equals(_lastActedRecipe, recipe, StringComparison.Ordinal);
        if (mine) Append(new Line(DateTimeOffset.UtcNow, "forced_shadow", RecipePrefix + recipe));
    }

    private void Append(Line line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_eventsFile)!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, LineOptions) + "\n");
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(_eventsFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                    stream.Write(bytes);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Counting only; a decision never depends on it.
        }
    }

    private List<Line> Lines()
    {
        var list = new List<Line>();
        if (!File.Exists(_eventsFile)) return list;
        try
        {
            using var stream = new FileStream(_eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } text)
            {
                try
                {
                    if (text.Length > 0 && JsonSerializer.Deserialize<Line>(text, LineOptions) is { Kind: not null } line) list.Add(line);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return list;
    }
}
