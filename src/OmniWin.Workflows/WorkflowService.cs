using System.Diagnostics;
using OmniWin.Core;

namespace OmniWin.Workflows;

/// <summary>One step of a run: what was asked, what came back, and how long OmniWin itself took.</summary>
/// <param name="ResolveMs">Time spent finding the element from its saved selector (0 for run_steps).</param>
/// <param name="OverheadMs">Step time excluding the app's reaction (the verify phase). This is the "workflow_run per step" benchmark figure (budget 20 ms).</param>
public sealed record StepRun(int Index, StepCall Call, ActResult Result, bool Passed, double ResolveMs, double TotalMs, double OverheadMs);

/// <summary>The outcome of run_steps or workflow_run.</summary>
public sealed record RunOutcome(
    string Tool,
    string? Name,
    int Total,
    IReadOnlyList<StepRun> Steps,
    int StoppedAt,                 // 1-based index of the step that stopped the run, 0 when none did
    ActStatus Status,
    string? Reason,
    string? Detail,
    bool InputSent,
    Observation? Observation,      // current observation after a stop, when one could be read
    RiskClass? Risk,
    double TotalMs);

public enum SaveStatus { Saved, Denied, Blocked }

public sealed record SaveOutcome(SaveStatus Status, string? Reason, string? Detail, string Name, int StepCount, IReadOnlyList<string> ParamNames);

/// <summary>
/// run_steps, workflow_save, workflow_list and workflow_run. Acts go through the normal engines, so the safety gate, the
/// stop key and every precheck run for each step exactly as for a single tool call. A step counts as passed when it ended
/// <c>confirmed</c>, or <c>unverified</c> with a non-empty diff (the same rule that decides what the run log keeps).
/// </summary>
public sealed class WorkflowService
{
    /// <summary>Metrics operation for the per-step figure (step time without the app's reaction).</summary>
    public const string StepMetric = "workflow_run_step";

    private static readonly TimeSpan ObserveAfterStop = TimeSpan.FromSeconds(3);

    private readonly StepRecorder _recorder;

    /// <summary>How long a replayed step keeps looking for its saved control (a window just opened may still be building it).</summary>
    internal static readonly TimeSpan SelectorSettle = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan SelectorPoll = TimeSpan.FromMilliseconds(150);
    private readonly WorkflowStore _store;
    private readonly ISafetyGate _gate;
    private readonly IKillSwitch _kill;
    private readonly IMetrics _metrics;
    private readonly IDesktopEngine? _desktop;
    private readonly IBrowserEngine? _browser;
    private readonly ISystemTools? _system;

    public WorkflowService(
        StepRecorder recorder,
        WorkflowStore store,
        ISafetyGate gate,
        IKillSwitch kill,
        IMetrics metrics,
        IDesktopEngine? desktop,
        IBrowserEngine? browser,
        ISystemTools? system)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _kill = kill ?? throw new ArgumentNullException(nameof(kill));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _desktop = desktop;
        _browser = browser;
        _system = system;
    }

    public IRunLog Log => _recorder.Log;

    public WorkflowStore Store => _store;

    public IReadOnlyList<WorkflowSummary> List() => _store.List();

    // ------------------------------------------------------------------ run_steps

    public async Task<RunOutcome> RunStepsAsync(IReadOnlyList<StepCall> steps, bool stopOnFailure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var sw = Stopwatch.StartNew();
        if (steps.Count == 0 || steps.Count > WorkflowStore.MaxSteps)
        {
            return Refused("run_steps", null, ReasonCodes.UnsupportedAction, $"run_steps takes 1 to {WorkflowStore.MaxSteps} steps.", sw);
        }

        var runs = new List<StepRun>(steps.Count);
        var anyInput = false;
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var call = steps[i];
                var run = await RunOneAsync(i + 1, call, saved: null, ct).ConfigureAwait(false);
                runs.Add(run);
                anyInput |= run.Result.InputSent;
                if (!run.Passed && stopOnFailure)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _kill.IsTripped)
        {
            return Interrupted("run_steps", null, steps.Count, runs, anyInput, sw);
        }

        return await FinishAsync("run_steps", null, steps.Count, runs, sw, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ workflow_run

    public async Task<RunOutcome> RunWorkflowAsync(string name, IReadOnlyDictionary<string, string>? values, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (loaded, workflow) = _store.Load(name);
        if (loaded.Status != StoreStatus.Ok || workflow is null)
        {
            return Refused("workflow_run", name, loaded.Status == StoreStatus.NotFound ? ReasonCodes.NotFound : ReasonCodes.UnsupportedAction, loaded.Detail ?? "The workflow could not be loaded.", sw);
        }

        // Every placeholder needs a value before anything runs.
        var missing = workflow.Params.Keys.Where(p => values is null || !values.ContainsKey(p)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            return Refused("workflow_run", name, ReasonCodes.UnsupportedAction, "Missing parameter value(s): " + string.Join(", ", missing) + ". Nothing was run.", sw);
        }

        var unknown = values?.Keys.Where(k => !workflow.Params.ContainsKey(k)).Order(StringComparer.Ordinal).ToList() ?? [];
        if (unknown.Count > 0)
        {
            return Refused("workflow_run", name, ReasonCodes.UnsupportedAction, "Unknown parameter(s): " + string.Join(", ", unknown) + ". Nothing was run.", sw);
        }

        // A file may have been edited by hand: check every step against the gate again before the first one runs.
        for (var i = 0; i < workflow.Steps.Count; i++)
        {
            if (WhyForbidden(workflow.Steps[i]) is { } why)
            {
                return Refused("workflow_run", name, ReasonCodes.PolicyForbidden, $"Step {i + 1} would be denied by the safety gate ({why}). Nothing was run.", sw, ActStatus.Denied);
            }
        }

        var runs = new List<StepRun>(workflow.Steps.Count);
        var anyInput = false;
        try
        {
            for (var i = 0; i < workflow.Steps.Count; i++)
            {
                var step = workflow.Steps[i];
                string? value = null;
                if (step.ValueParam is not null) value = values![step.ValueParam];
                var call = StepTools.ToCall(step.Tool, step.Action, step.Args, value);
                var run = await RunOneAsync(i + 1, call, step, ct).ConfigureAwait(false);
                runs.Add(run);
                anyInput |= run.Result.InputSent;
                if (!run.Passed)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _kill.IsTripped)
        {
            return Interrupted("workflow_run", name, workflow.Steps.Count, runs, anyInput, sw);
        }

        return await FinishAsync("workflow_run", name, workflow.Steps.Count, runs, sw, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ one step

    private async Task<StepRun> RunOneAsync(int index, StepCall call, WorkflowStep? saved, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var resolveMs = 0.0;

        if (_kill.IsTripped)
        {
            return Stopped(index, call, ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0)), sw, 0);
        }

        // A saved step has no ref yet: it gets one from its selector below, and is validated after that.
        if (saved is null && Validate(call) is { } invalid)
        {
            return Stopped(index, call, ActResult.Blocked(ReasonCodes.UnsupportedAction, invalid, Timing.Total(0)), sw, 0);
        }

        StepContext context;
        if (saved is not null)
        {
            // Saved step: find the element again from its selector, then act on the ref and generation the engine returns.
            string? windowProcess = saved.Args.TryGetValue("window", out var w) ? w : null;
            context = new StepContext(saved.Target, windowProcess);
            if (saved.Target is not null)
            {
                var swResolve = Stopwatch.StartNew();
                var resolved = await ResolveAsync(saved.Target, ct).ConfigureAwait(false);

                // A window the previous step just opened may not have built its controls yet: look again for a short while before
                // calling the step stale. Nothing is sent while looking; a control that is really gone still stops the run.
                while (resolved is null && swResolve.Elapsed < SelectorSettle && !_kill.IsTripped)
                {
                    await Task.Delay(SelectorPoll, ct).ConfigureAwait(false);
                    resolved = await ResolveAsync(saved.Target, ct).ConfigureAwait(false);
                }

                resolveMs = swResolve.Elapsed.TotalMilliseconds;
                if (resolved is null)
                {
                    return Stopped(index, call, new ActResult(ActStatus.Stale, false, ReasonCodes.NotFound,
                        $"Step {index}: the saved selector matched nothing on screen now.", null, null, Timing.Total(resolveMs)), sw, resolveMs);
                }

                call = call with { Ref = resolved.Ref, Gen = resolved.Generation };

                // Contract 2.6 (R1): the first run typed into an empty field. If it has text now, typing would add to text the
                // recipe never saw (perhaps the owner's own document), so stop here with nothing sent and let the AI decide.
                if (saved.TargetEmptyBefore == true && saved.Tool == "desktop_act" && saved.Action == "type" && _desktop is IFieldProbe probe &&
                    await probe.FieldIsEmptyAsync(resolved.Ref, ct).ConfigureAwait(false) == false)
                {
                    return Stopped(index, call, new ActResult(ActStatus.Stale, false, ReasonCodes.FieldNotEmpty,
                        "The field already has text; the first run typed into an empty field. Nothing was typed.", null, null, Timing.Total(resolveMs)), sw, resolveMs);
                }
            }
        }
        else
        {
            context = await _recorder.PrepareAsync(call, ct).ConfigureAwait(false);
        }

        if (saved is not null && Validate(call) is { } savedInvalid)
        {
            return Stopped(index, call, ActResult.Blocked(ReasonCodes.UnsupportedAction, $"Step {index}: {savedInvalid}", Timing.Total(resolveMs)), sw, resolveMs);
        }

        var result = await ExecuteAsync(call, ct).ConfigureAwait(false);

        // The control moved between finding it and acting (a menu still sliding open): nothing was sent, so find it again and retry.
        for (var retry = 0; saved?.Target is not null && result.Status == ActStatus.Stale && !result.InputSent && retry < 3 && !_kill.IsTripped; retry++)
        {
            await Task.Delay(SelectorPoll * 2, ct).ConfigureAwait(false);
            var again = await ResolveAsync(saved.Target, ct).ConfigureAwait(false);
            if (again is null) break;
            call = call with { Ref = again.Ref, Gen = again.Generation };
            result = await ExecuteAsync(call, ct).ConfigureAwait(false);
        }

        var hasDiff = StepRecorder.HasDiffLines(result.After);
        var passed = StepRecorder.IsRecordable(result.Status, hasDiff);
        if (passed)
        {
            var stepId = _recorder.Record(call, context, result.Status, hasDiff, result.ExpectOutcome);
            if (stepId is not null) result = result with { StepId = stepId };
        }

        var verifyMs = result.Timing.PhasesMs.TryGetValue("verify", out var v) ? v : 0;
        var total = sw.Elapsed.TotalMilliseconds;
        var overhead = Math.Max(0, total - verifyMs);
        _metrics.Record(StepMetric, overhead);
        return new StepRun(index, call, result, passed, resolveMs, total, overhead);
    }

    private static StepRun Stopped(int index, StepCall call, ActResult result, Stopwatch sw, double resolveMs) =>
        new(index, call, result, false, resolveMs, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds);

    /// <summary>Why a step cannot run, or null.</summary>
    internal static string? Validate(StepCall call)
    {
        if (!StepTools.IsKnown(call.Tool))
        {
            return $"\"{call.Tool}\" is not a tool a step may call. Allowed: {string.Join(", ", StepTools.Names.Order(StringComparer.Ordinal))}.";
        }

        return call.Tool switch
        {
            "desktop_act" or "browser_act" when string.IsNullOrEmpty(call.Ref) || call.Gen is null || string.IsNullOrEmpty(call.Action) =>
                $"{call.Tool} needs ref, gen and action.",
            "desktop_keys" when string.IsNullOrWhiteSpace(call.Keys) => "desktop_keys needs keys.",
            "desktop_scroll" when string.IsNullOrEmpty(call.Direction) || call.Amount is null => "desktop_scroll needs direction and amount.",
            "browser_nav" when string.IsNullOrEmpty(call.Action) => "browser_nav needs action.",
            "app_launch" when string.IsNullOrWhiteSpace(call.App) => "app_launch needs app.",
            "window_manage" when string.IsNullOrWhiteSpace(call.Window) || string.IsNullOrEmpty(call.Action) => "window_manage needs window and action.",
            "media" when string.IsNullOrEmpty(call.Action) => "media needs action.",
            "clipboard_set" when call.Text is null => "clipboard_set needs text.",
            _ => null,
        };
    }

    private async Task<ResolvedRef?> ResolveAsync(ElementDescriptor target, CancellationToken ct)
    {
        if (target.Engine == StepTools.Browser)
        {
            return _browser is null ? null : await _browser.ResolveSelectorAsync(UrlPattern(target.Url), target.Path, ct).ConfigureAwait(false);
        }

        return _desktop is null ? null : await _desktop.ResolveSelectorAsync(target.Process, target.WindowTitle, target.Path, ct).ConfigureAwait(false);
    }

    /// <summary>The page address without its query or fragment, so a saved selector carries no tokens from a URL.</summary>
    public static string UrlPattern(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    private async Task<ActResult> ExecuteAsync(StepCall call, CancellationToken ct)
    {
        switch (call.Tool)
        {
            case "desktop_act":
                return _desktop is null ? NoEngine("desktop") : await _desktop.ActAsync(new DesktopActRequest(call.Ref!, call.Gen!.Value, call.Action!, call.Value, call.Expect), ct).ConfigureAwait(false);
            case "desktop_keys":
                return _desktop is null ? NoEngine("desktop") : await _desktop.SendKeysAsync(new KeysRequest(call.Keys!, call.Window, call.Expect), ct).ConfigureAwait(false);
            case "desktop_scroll":
                return _desktop is null ? NoEngine("desktop") : await _desktop.ScrollAsync(new ScrollRequest(call.Ref, call.Window, call.Direction!, call.Amount!.Value), ct).ConfigureAwait(false);
            case "browser_act":
                return _browser is null ? NoEngine("browser") : await _browser.ActAsync(new BrowserActRequest(call.Ref!, call.Gen!.Value, call.Action!, call.Value, call.Expect), ct).ConfigureAwait(false);
            case "browser_nav":
                return _browser is null ? NoEngine("browser") : await _browser.NavigateAsync(new BrowserNavRequest(call.Action!, call.Url, call.Tab, call.Expect), ct).ConfigureAwait(false);
            case "app_launch":
                return _system is null ? NoEngine("system tools") : await _system.LaunchAppAsync(call.App!, call.Args, call.Expect, ct).ConfigureAwait(false);
            case "window_manage":
                return _system is null ? NoEngine("system tools") : await _system.WindowManageAsync(new WindowManageRequest(call.Window!, call.Action!, call.X, call.Y, call.Width, call.Height), ct).ConfigureAwait(false);
            case "audio":
                return _system is null ? NoEngine("system tools") : await _system.SetAudioAsync(call.Volume, call.Mute, ct).ConfigureAwait(false);
            case "media":
                return _system is null ? NoEngine("system tools") : await _system.MediaAsync(call.Action!, ct).ConfigureAwait(false);
            case "clipboard_set":
                return _system is null ? NoEngine("system tools") : await _system.ClipboardSetAsync(call.Text!, ct).ConfigureAwait(false);
            default:
                return ActResult.Blocked(ReasonCodes.UnsupportedAction, $"\"{call.Tool}\" is not a tool a step may call.", Timing.Total(0));
        }
    }

    private static ActResult NoEngine(string engine) =>
        ActResult.Blocked(engine == "browser" ? ReasonCodes.BrowserNotConnected : ReasonCodes.UnsupportedAction,
            $"The {engine} engine is not available in this server.", Timing.Total(0));

    // ------------------------------------------------------------------ finishing a run

    private async Task<RunOutcome> FinishAsync(string tool, string? name, int total, List<StepRun> runs, Stopwatch sw, CancellationToken ct)
    {
        var anyInput = runs.Any(r => r.Result.InputSent);
        var risky = runs.Any(r => r.Result.Risk == RiskClass.Risky);
        var failed = runs.FirstOrDefault(r => !r.Passed);
        if (failed is null)
        {
            // A run is confirmed only when every step was confirmed. A step that passed as "unverified with a visible change"
            // (for example set_value on a file dialog's File name box) lets the run continue, but the run itself is then
            // unverified and names those steps.
            var unconfirmed = runs.Where(r => r.Result.Status != ActStatus.Confirmed).ToList();
            if (unconfirmed.Count == 0)
            {
                return new RunOutcome(tool, name, total, runs, 0, ActStatus.Confirmed, null, null, anyInput, null, risky ? RiskClass.Risky : null, sw.Elapsed.TotalMilliseconds);
            }

            var named = string.Join(", ", unconfirmed.Select(r =>
                $"step {r.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({OmniWin.Core.ResultFormatter.StatusName(r.Result.Status)})"));
            return new RunOutcome(tool, name, total, runs, 0, ActStatus.Unverified, null,
                $"Every step ran, but not every step was confirmed: {named}.", anyInput, null, risky ? RiskClass.Risky : null, sw.Elapsed.TotalMilliseconds);
        }

        Observation? observation = null;
        if (!ct.IsCancellationRequested && !_kill.IsTripped)
        {
            observation = await ObserveAsync(failed.Call, ct).ConfigureAwait(false);
        }

        return new RunOutcome(tool, name, total, runs, failed.Index, failed.Result.Status, failed.Result.Reason, failed.Result.Detail,
            anyInput, observation, risky ? RiskClass.Risky : null, sw.Elapsed.TotalMilliseconds);
    }

    private async Task<Observation?> ObserveAsync(StepCall call, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ObserveAfterStop);
        try
        {
            switch (StepTools.EngineOf(call.Tool))
            {
                case StepTools.Browser when _browser is not null:
                    return await _browser.ObserveAsync(new BrowserObserveRequest(), cts.Token).ConfigureAwait(false);
                case StepTools.Desktop when _desktop is not null:
                    return await _desktop.ObserveAsync(new DesktopObserveRequest(Window: call.Window), cts.Token).ConfigureAwait(false);
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;   // the run result stands without an observation
        }
    }

    private RunOutcome Interrupted(string tool, string? name, int total, List<StepRun> runs, bool anyInput, Stopwatch sw)
    {
        var risky = runs.Any(r => r.Result.Risk == RiskClass.Risky);
        var risk = risky ? RiskClass.Risky : (RiskClass?)null;
        if (!anyInput && _kill.IsTripped)
        {
            return new RunOutcome(tool, name, total, runs, runs.Count + 1, ActStatus.Denied, ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", false, null, risk, sw.Elapsed.TotalMilliseconds);
        }

        var reason = _kill.IsTripped || anyInput ? ReasonCodes.InputInterrupted : ReasonCodes.EngineTimeout;
        var detail = _kill.IsTripped
            ? "The run was stopped by the stop key after some input was sent."
            : anyInput ? "The run was cancelled after some input was sent." : "The run took too long and was cancelled before any input was sent.";
        return new RunOutcome(tool, name, total, runs, runs.Count + 1, ActStatus.Error, reason, detail, anyInput, null, risk, sw.Elapsed.TotalMilliseconds);
    }

    private static RunOutcome Refused(string tool, string? name, string reason, string detail, Stopwatch sw, ActStatus status = ActStatus.Blocked) =>
        new(tool, name, 0, [], 0, status, reason, detail, false, null, null, sw.Elapsed.TotalMilliseconds);

    // ------------------------------------------------------------------ workflow_save

    /// <summary>
    /// Saves run-log steps <paramref name="fromStep"/> to <paramref name="toStep"/> as a workflow. Every step is checked
    /// against the safety gate first; a workflow with a step the gate would deny is not saved. <paramref name="renames"/>
    /// maps a step placeholder ("p3") to the parameter name to use ("search_text").
    /// </summary>
    public SaveOutcome Save(string name, string? description, string fromStep, string toStep, IReadOnlyDictionary<string, string>? renames)
    {
        if (!WorkflowStore.IsValidName(name))
        {
            return new SaveOutcome(SaveStatus.Blocked, ReasonCodes.UnsupportedAction, "A workflow name is 1 to 64 letters, digits, '_' or '-', starting with a letter or digit.", name, 0, []);
        }

        var range = _recorder.Log.Range(fromStep, toStep);
        if (range is null)
        {
            return new SaveOutcome(SaveStatus.Blocked, ReasonCodes.NotFound, "from_step and to_step must be retained run-log step ids, with from_step first.", name, 0, []);
        }

        if (range.Count > WorkflowStore.MaxSteps)
        {
            return new SaveOutcome(SaveStatus.Blocked, ReasonCodes.UnsupportedAction, $"A workflow has at most {WorkflowStore.MaxSteps} steps.", name, 0, []);
        }

        var placeholders = range.Where(s => s.ValueParam is not null).Select(s => s.ValueParam!).ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var placeholder in placeholders) map[placeholder] = placeholder;
        if (renames is not null)
        {
            foreach (var (from, to) in renames)
            {
                if (!placeholders.Contains(from))
                {
                    return new SaveOutcome(SaveStatus.Blocked, ReasonCodes.NotFound, $"params names \"{from}\", which is not a placeholder in steps {fromStep} to {toStep}. Placeholders: {string.Join(", ", placeholders.Order(StringComparer.Ordinal))}.", name, 0, []);
                }

                if (!WorkflowStore.IsValidParamName(to))
                {
                    return new SaveOutcome(SaveStatus.Blocked, ReasonCodes.UnsupportedAction, $"\"{to}\" is not a valid parameter name (lower case letter first, then lower case letters, digits or '_', up to 32).", name, 0, []);
                }

                map[from] = to;
            }
        }

        var steps = new List<WorkflowStep>(range.Count);
        var paramDescriptions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < range.Count; i++)
        {
            var s = range[i];
            string? param = s.ValueParam is null ? null : map[s.ValueParam];
            var step = new WorkflowStep(s.Tool, s.Engine, s.Action, s.Target, s.Args, param, s.TargetEmptyBefore);
            if (WhyForbidden(step) is { } why)
            {
                return new SaveOutcome(SaveStatus.Denied, ReasonCodes.PolicyForbidden, $"Step {i + 1} ({s.Tool} {s.Action}) would be denied by the safety gate: {why} Nothing was saved.", name, 0, []);
            }

            steps.Add(step);
            if (param is not null) paramDescriptions[param] = $"step {i + 1} {s.Tool} value";
        }

        var workflow = new Workflow(WorkflowStore.CurrentVersion, name, ResultFormatter.OneLine(description ?? string.Empty), DateTimeOffset.UtcNow, paramDescriptions, steps);
        var stored = _store.Save(workflow);
        return stored.Status switch
        {
            StoreStatus.Ok => new SaveOutcome(SaveStatus.Saved, null, null, name, steps.Count, [.. paramDescriptions.Keys]),
            StoreStatus.Exists => new SaveOutcome(SaveStatus.Blocked, ReasonCodes.UnsupportedAction, stored.Detail, name, 0, []),
            _ => new SaveOutcome(SaveStatus.Blocked, ReasonCodes.EngineTimeout, stored.Detail, name, 0, []),
        };
    }

    /// <summary>
    /// Contract 2.6 (learning): saves <paramref name="prefix"/> (steps kept from an earlier version, may be empty) followed by the
    /// run-log steps in <paramref name="range"/> as a new workflow. Same rules as <see cref="Save"/>: every step is checked against
    /// the gate, nothing is replaced. <paramref name="renames"/> maps a run-log placeholder ("p3") to a parameter name.
    /// </summary>
    public SaveOutcome SaveLearned(string name, string? description, IReadOnlyList<WorkflowStep> prefix, IReadOnlyList<RunStep> range,
        IReadOnlyDictionary<string, string>? renames)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(range);
        if (!WorkflowStore.IsValidName(name))
        {
            return new SaveOutcome(SaveStatus.Blocked, ReasonCodes.UnsupportedAction, "Not a valid workflow name.", name, 0, []);
        }

        var total = prefix.Count + range.Count;
        if (total is 0 or > WorkflowStore.MaxSteps)
        {
            return new SaveOutcome(SaveStatus.Blocked, ReasonCodes.UnsupportedAction, $"A workflow has 1 to {WorkflowStore.MaxSteps} steps.", name, 0, []);
        }

        var steps = new List<WorkflowStep>(total);
        var paramDescriptions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kept in prefix)
        {
            steps.Add(kept);
            if (kept.ValueParam is not null) paramDescriptions[kept.ValueParam] = $"step {steps.Count} {kept.Tool} value";
        }

        foreach (var s in range)
        {
            string? param = null;
            if (s.ValueParam is not null)
            {
                param = renames is not null && renames.TryGetValue(s.ValueParam, out var renamed) ? renamed : s.ValueParam;
                if (!WorkflowStore.IsValidParamName(param))
                {
                    return new SaveOutcome(SaveStatus.Blocked, ReasonCodes.UnsupportedAction, $"\"{param}\" is not a valid parameter name.", name, 0, []);
                }
            }

            steps.Add(new WorkflowStep(s.Tool, s.Engine, s.Action, s.Target, s.Args, param, s.TargetEmptyBefore));
            if (param is not null) paramDescriptions[param] = $"step {steps.Count} {s.Tool} value";
        }

        for (var i = 0; i < steps.Count; i++)
        {
            if (WhyForbidden(steps[i]) is { } why)
            {
                return new SaveOutcome(SaveStatus.Denied, ReasonCodes.PolicyForbidden, $"Step {i + 1} ({steps[i].Tool} {steps[i].Action}) would be denied by the safety gate: {why} Nothing was saved.", name, 0, []);
            }
        }

        var workflow = new Workflow(WorkflowStore.CurrentVersion, name, ResultFormatter.OneLine(description ?? string.Empty), DateTimeOffset.UtcNow, paramDescriptions, steps);
        var stored = _store.Save(workflow);
        return stored.Status switch
        {
            StoreStatus.Ok => new SaveOutcome(SaveStatus.Saved, null, null, name, steps.Count, [.. paramDescriptions.Keys]),
            StoreStatus.Exists => new SaveOutcome(SaveStatus.Blocked, ReasonCodes.UnsupportedAction, stored.Detail, name, 0, []),
            _ => new SaveOutcome(SaveStatus.Blocked, ReasonCodes.EngineTimeout, stored.Detail, name, 0, []),
        };
    }

    /// <summary>How the safety gate classes a saved step, from what is stored (see <see cref="WhyForbidden"/>).</summary>
    public RiskClass ClassifyStep(WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var args = step.Args;
        string? Arg(string key) => args.TryGetValue(key, out var v) ? v : null;
        return _gate.Classify(new ActionDescriptor(step.Tool, step.Engine, step.Action, step.Target,
            step.Target?.Process ?? Arg("window"), step.Target?.WindowTitle, step.Target?.Url ?? Arg("url"), null, Arg("keys")));
    }

    /// <summary>
    /// Asks the safety gate whether a saved step could ever be allowed, using only what is stored (the target element, the
    /// keys, the window's process, the address). Text is not stored, so a typed delete command is still caught when the step
    /// runs, by the engine's own gate call.
    /// </summary>
    internal string? WhyForbidden(WorkflowStep step)
    {
        var args = step.Args;
        string? Arg(string key) => args.TryGetValue(key, out var v) ? v : null;
        var descriptor = new ActionDescriptor(
            step.Tool,
            step.Engine,
            step.Action,
            step.Target,
            step.Target?.Process ?? Arg("window"),
            step.Target?.WindowTitle,
            step.Target?.Url ?? Arg("url"),
            null,
            Arg("keys"));
        return _gate.Classify(descriptor) == RiskClass.Forbidden
            ? "the policy forbids this action (OmniWin never deletes)."
            : null;
    }
}
