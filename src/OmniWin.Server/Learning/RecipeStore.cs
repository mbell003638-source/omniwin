using System.Text;
using System.Text.Json;
using OmniWin.Workflows;

namespace OmniWin.Server.Learning;

/// <summary>
/// What OmniWin remembers about a learned workflow (contract 2.6). Written once beside the workflow and never changed: everything
/// that changes later (replays, trust, extra wordings, disabling) is an appended event.
/// </summary>
/// <param name="Goal">The request wording the task was learned from, normalised.</param>
/// <param name="Template">The wording with <c>{slot}</c> holes, or null when the recipe has text parameters and no wording for them yet.</param>
/// <param name="LearnedWallMs">How long the first run took from the request to task_done, the AI's thinking included.</param>
/// <param name="AutoRunEligible">False when a step is risky or runs in the browser: such a recipe is never run without the AI.</param>
/// <param name="Noisy">The first run had a failed or blocked act in it.</param>
/// <param name="Supersedes">The earlier version this one repaired, or null.</param>
internal sealed record RecipeCard(
    int Version,
    string Workflow,
    string Goal,
    string? Template,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Params,
    int Steps,
    double LearnedWallMs,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Processes,
    bool AutoRunEligible,
    bool Noisy,
    string? Supersedes,
    DateTimeOffset LearnedAt);

/// <summary>One appended line of <c>recipes\events.jsonl</c>.</summary>
internal sealed record RecipeEvent(DateTimeOffset At, string Recipe, string Kind, int? Step = null, double? WallMs = null, string? Alias = null, string? By = null);

internal static class RecipeEventKinds
{
    public const string Learned = "learned";
    public const string ReplayOk = "replay_ok";
    public const string ReplayStopped = "replay_stopped";
    public const string Repaired = "repaired";
    public const string Superseded = "superseded";
    public const string Disabled = "disabled";
    public const string Enabled = "enabled";
    public const string AliasAdded = "alias_added";
    public const string Right = "right";
    public const string WrongPick = "wrong_pick";
}

/// <summary>A recipe as it stands now: its card plus what the events say.</summary>
internal sealed record RecipeState(
    RecipeCard Card,
    bool Trusted,
    bool Disabled,
    bool Superseded,
    int CleanInARow,
    int Runs,
    int Stops,
    string? Last,
    double? MedianReplayMs,
    IReadOnlyList<string> Templates)
{
    public string Name => Card.Workflow;

    public bool Active => !Disabled && !Superseded;

    public string Trust => Superseded ? "superseded" : Disabled ? "disabled" : Trusted ? "trusted" : "candidate";
}

/// <summary>
/// Recipe cards and the events file. Append-only: a card is created once (never replaced), events are only ever added, and nothing is
/// deleted. Two OmniWin processes may share the folder, so every write opens, writes one line and closes, with a short retry.
/// </summary>
internal sealed class RecipeStore(string directory, TimeProvider? time = null)
{
    public const int CardVersion = 1;
    public const int MaxAliases = 10;
    private const string CardSuffix = ".recipe.json";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();

    public string Directory => directory;

    private string EventsFile => Path.Combine(directory, "events.jsonl");

    private string CardFile(string name) => Path.Combine(directory, name + CardSuffix);

    public DateTimeOffset Now => _time.GetUtcNow();

    public bool SaveCard(RecipeCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!WorkflowStore.IsValidName(card.Workflow)) return false;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            using var stream = new FileStream(CardFile(card.Workflow), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, card, StepTools.JsonOptions);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public RecipeCard? LoadCard(string name)
    {
        if (!WorkflowStore.IsValidName(name)) return null;
        var path = CardFile(name);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var card = JsonSerializer.Deserialize<RecipeCard>(stream, StepTools.JsonOptions);
            return card is { Version: CardVersion } && string.Equals(card.Workflow, name, StringComparison.Ordinal) ? card : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Appends one event. Returns false when the line could not be written (the other OmniWin process held the file for too long, or
    /// the folder cannot be written). A run's result never depends on it, but a caller whose whole job is the event (workflow_manage)
    /// must say so instead of answering "recorded" (review 27, M6).
    /// </summary>
    public bool Append(string recipe, string kind, int? step = null, double? wallMs = null, string? alias = null, string? by = null)
    {
        var line = JsonSerializer.Serialize(new RecipeEvent(Now, recipe, kind, step, wallMs is { } ms ? Math.Round(ms, 1) : null, alias, by), LineOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (_gate)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(directory);
                    using var stream = new FileStream(EventsFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                    stream.Write(bytes);
                    return true;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(10);   // the other OmniWin process is appending its own line
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }
    }

    private static readonly JsonSerializerOptions LineOptions = new(StepTools.JsonOptions)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyList<RecipeEvent> Events()
    {
        var list = new List<RecipeEvent>();
        if (!File.Exists(EventsFile)) return list;
        try
        {
            using var stream = new FileStream(EventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<RecipeEvent>(line, LineOptions) is { Recipe: not null, Kind: not null } e) list.Add(e);
                }
                catch (JsonException)
                {
                    // A torn or hand-edited line is skipped; the rest of the history still counts.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable right now: recipes read as candidates, which is the safe side.
        }

        return list;
    }

    /// <summary>Every recipe with a readable card, with its state computed from the events.</summary>
    public IReadOnlyList<RecipeState> All(int promoteAfter)
    {
        var result = new List<RecipeState>();
        if (!System.IO.Directory.Exists(directory)) return result;
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(directory, "*" + CardSuffix).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        var byRecipe = Events().GroupBy(e => e.Recipe, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file)[..^CardSuffix.Length];
            if (LoadCard(name) is not { } card) continue;
            result.Add(StateOf(card, byRecipe.GetValueOrDefault(name) ?? [], promoteAfter));
        }

        return result;
    }

    public RecipeState? Get(string name, int promoteAfter) =>
        LoadCard(name) is { } card ? StateOf(card, [.. Events().Where(e => string.Equals(e.Recipe, name, StringComparison.Ordinal))], promoteAfter) : null;

    internal static RecipeState StateOf(RecipeCard card, IReadOnlyList<RecipeEvent> events, int promoteAfter)
    {
        var clean = 0;
        var runs = 0;
        var stops = 0;
        var disabled = false;
        var superseded = false;
        string? last = null;
        var replayMs = new List<double>();
        var templates = new List<string>();
        if (card.Template is not null) templates.Add(card.Template);
        templates.AddRange(card.Aliases);
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case RecipeEventKinds.ReplayOk:
                    clean++;
                    runs++;
                    last = e.Kind;
                    if (e.WallMs is { } ms) replayMs.Add(ms);
                    break;
                case RecipeEventKinds.Right:
                    clean++;
                    break;
                case RecipeEventKinds.ReplayStopped:
                    clean = 0;
                    runs++;
                    stops++;
                    last = e.Kind;
                    break;
                case RecipeEventKinds.WrongPick:
                    clean = 0;
                    last = e.Kind;
                    break;
                case RecipeEventKinds.Disabled:
                    disabled = true;
                    break;
                case RecipeEventKinds.Enabled:
                    disabled = false;
                    break;
                case RecipeEventKinds.Superseded:
                    superseded = true;
                    break;
                case RecipeEventKinds.AliasAdded when e.Alias is { Length: > 0 } alias && !templates.Contains(alias, StringComparer.Ordinal) && templates.Count < MaxAliases + 1:
                    templates.Add(alias);
                    break;
            }
        }

        replayMs.Sort();
        double? median = replayMs.Count == 0 ? null : replayMs[replayMs.Count / 2];
        return new RecipeState(card, clean >= promoteAfter, disabled, superseded, clean, runs, stops, last, median, templates);
    }
}
