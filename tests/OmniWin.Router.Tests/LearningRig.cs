using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Learning;
using OmniWin.Server.Router;
using OmniWin.Workflows;

namespace OmniWin.Router.Tests;

/// <summary>A pretend screen: named controls that are either there or not. Records every act, so a test can say exactly what was sent.</summary>
internal sealed class ScreenDesktop : IDesktopEngine, IFieldProbe
{
    /// <summary>Off by default, so the other tests see no field contents (as with an app OmniWin cannot read).</summary>
    public bool ProbeFields { get; set; }

    /// <summary>The text in each field, by control name. A type act adds to it, as real typing does.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<bool?> FieldIsEmptyAsync(string elementRef, CancellationToken ct) =>
        Task.FromResult<bool?>(!ProbeFields || !Refs.TryGetValue(elementRef, out var d) ? null : Values.GetValueOrDefault(d.Name, string.Empty).Length == 0);

    private int _next = 1;

    public Dictionary<string, ElementDescriptor> Refs { get; } = new(StringComparer.Ordinal);

    public HashSet<string> OnScreen { get; } = new(StringComparer.Ordinal);

    public List<(string Name, string Action, string? Value)> Acts { get; } = [];

    /// <summary>Names whose act comes back unverified with nothing visible (so it is not recorded).</summary>
    public HashSet<string> Silent { get; } = new(StringComparer.Ordinal);

    public string Add(string name, string role = "button", string process = "formapp.exe")
    {
        var reference = "e" + _next++;
        Refs[reference] = new ElementDescriptor(reference, "desktop", process, "Form", null, role, name, null, false, true,
            [new SelectorSegment("window", null, "Form", null, null), new SelectorSegment(role, null, name, null, null)]);
        OnScreen.Add(name);
        return reference;
    }

    public string RefOf(string name) => Refs.First(r => r.Value.Name == name).Key;

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WindowInfo>>([]);

    public Task<Observation> ObserveAsync(DesktopObserveRequest req, CancellationToken ct) =>
        Task.FromResult(new Observation("w1", 1, "w1 gen=1", 0, 0, false, null, Timing.Total(0)));

    public Task<ActResult> ActAsync(DesktopActRequest req, CancellationToken ct)
    {
        if (!Refs.TryGetValue(req.Ref, out var target) || !OnScreen.Contains(target.Name))
        {
            return Task.FromResult(new ActResult(ActStatus.Stale, false, ReasonCodes.NotFound, "gone", null, null, Timing.Total(1)));
        }

        Acts.Add((target.Name, req.Action, req.Value));
        if (req.Action == "type") Values[target.Name] = Values.GetValueOrDefault(target.Name, string.Empty) + req.Value;
        return Task.FromResult(Silent.Contains(target.Name)
            ? new ActResult(ActStatus.Unverified, true, null, null, null, Channels.Invoke, Timing.Total(1))
            : new ActResult(ActStatus.Confirmed, true, null, null, null, Channels.Invoke, Timing.Total(1)));
    }

    public Task<ActResult> SendKeysAsync(KeysRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> ScrollAsync(ScrollRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<WaitResult> WaitForAsync(Expectation condition, int timeoutMs, CancellationToken ct) => throw new NotSupportedException();

    public Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct) =>
        Task.FromResult(Refs.TryGetValue(elementRef, out var d) ? d : null);

    public Task<ResolvedRef?> ResolveSelectorAsync(string process, string windowTitlePattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct)
    {
        var name = path[^1].Name;
        return Task.FromResult(name is not null && OnScreen.Contains(name) ? new ResolvedRef(RefOf(name), 1) : null);
    }
}

/// <summary>A gate whose risk class a test can set per control name.</summary>
internal sealed class NamedGate : ISafetyGate
{
    public HashSet<string> Risky { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Forbidden { get; } = new(StringComparer.Ordinal);

    public RiskClass Classify(ActionDescriptor a) =>
        a.Target is { } t && Forbidden.Contains(t.Name) ? RiskClass.Forbidden : a.Target is { } r && Risky.Contains(r.Name) ? RiskClass.Risky : RiskClass.Normal;

    public Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct) => Task.FromResult(new GateResult(true, Classify(a), null, false));
}

/// <summary>Everything learning needs, wired like the server wires it, on a pretend screen and a temp folder the rig removes.</summary>
internal sealed class LearningRig : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "omniwin-test-learn-" + Guid.NewGuid().ToString("N"));

    public LearningRig(OmniWinConfig? config = null, IDecisionModel? model = null)
    {
        Config = config ?? new OmniWinConfig();
        Log = new RunLog();
        var recorder = new StepRecorder(Log, Screen, null);
        Workflows = new WorkflowService(recorder, new WorkflowStore(Path.Combine(_dir, "workflows")), Gate, new FreeKill(), new NullMetrics(), Screen, null, System);
        Recipes = new RecipeStore(Path.Combine(_dir, "recipes"));
        Learning = new LearningService(Workflows, Log, Recipes, Config);
        Decision = new DecisionService(Config, Learning, model, Path.Combine(_dir, "recipes"), () => ConfigEditedAt);
        Router = new RouterService(System, Screen, new WorkflowCatalog(Workflows), Config, new NullMetrics(), null, Learning, Decision);
    }

    public OmniWinConfig Config { get; }

    public ScreenDesktop Screen { get; } = new();

    public NamedGate Gate { get; } = new();

    public FakeSystem System { get; } = new();

    public RunLog Log { get; }

    public WorkflowService Workflows { get; }

    public RecipeStore Recipes { get; }

    public LearningService Learning { get; }

    public RouterService Router { get; }

    public DecisionService Decision { get; }

    /// <summary>When the pretend config.json was last edited by the owner.</summary>
    public DateTimeOffset? ConfigEditedAt { get; set; }

    public string RecipesDir => Recipes.Directory;

    public string WorkflowsDir => Workflows.Store.Directory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Task<string> Ask(string text) => Router.DoAsync(text, Ct);

    /// <summary>The AI does acts itself, one tool call each, as the server's filter would see them.</summary>
    public async Task Do(params (string Name, string Action, string? Value)[] acts)
    {
        foreach (var (name, action, value) in acts)
        {
            var run = await Workflows.RunStepsAsync([new StepCall("desktop_act", Ref: Screen.RefOf(name), Gen: 1, Action: action, Value: value)], true, Ct);
            Learning.NoteAct(run.Steps.Count == 1 && run.Steps[0].Passed);
        }
    }

    public Task<RunOutcome> Replay(string name, Dictionary<string, string>? values = null) => Learning.RunAsync(name, values, null, Ct);

    public RecipeState State(string name) => Recipes.Get(name, Config.Learning.PromoteAfter)!;

    /// <summary>Learns the standard three-step form task and returns the recipe name.</summary>
    public async Task<string> LearnForm(string who = "bob")
    {
        if (Screen.Refs.Count == 0)
        {
            Screen.Add("Name", "edit");
            Screen.Add("Agree", "checkbox");
            Screen.Add("Apply");
        }

        await Ask($"fill the form for {who}");
        await Do(("Name", "type", who), ("Agree", "toggle", null), ("Apply", "click", null));
        var placeholder = Log.Steps.Last(s => s.ValueParam is not null).ValueParam!;
        var answer = Learning.TaskDone(true, null, "fill the form for {name}", new Dictionary<string, string> { [placeholder] = "name" });
        Assert.Contains("learned=true", answer, StringComparison.Ordinal);
        return Token(answer, "recipe=");
    }

    public async Task Trust(string name, string value = "x")
    {
        for (var i = 0; i < Config.Learning.PromoteAfter; i++)
        {
            var run = await Replay(name, State(name).Card.Params.Count == 0 ? null : new Dictionary<string, string> { [State(name).Card.Params[0]] = value });
            Assert.Equal(ActStatus.Confirmed, run.Status);
        }

        Assert.True(State(name).Trusted);
    }

    public static string Token(string text, string key)
    {
        var first = text.Split('\n')[0];
        var token = first.Split(' ').First(t => t.StartsWith(key, StringComparison.Ordinal));
        return token[key.Length..];
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);   // the temp folder this rig created
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
