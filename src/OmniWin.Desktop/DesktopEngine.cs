using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Desktop;

public sealed class DesktopEngine : IDesktopEngine, IForegroundService, IForegroundDetails, ILabelSource, IInputLane, ISelectionSource, IFieldProbe, IComponentStatus, IAsyncDisposable
{
    private readonly UiaWorker _worker;
    private readonly UiaEvents _events;
    private readonly OmniWinConfig _config;
    private readonly ISafetyGate _gate;
    private readonly IKillSwitch _killSwitch;
    private readonly IUserActivity? _activity;
    private readonly IOcrRefResolver? _ocr;
    private readonly IWindowTextReader? _screenText;
    private readonly IMetrics _metrics;
    private readonly ILogger<DesktopEngine> _log;
    // Which acts may overlap: real mouse/keyboard input is one lane; pattern acts on different windows run together.
    private readonly ActLanes _lanes = new();

    /// <summary>
    /// Parallel desktop lanes are OFF by default until the reviewer has checked them (the owner asked for the work, the
    /// reviewer has not signed it off). With them off, every act, pattern or physical, takes the physical lane, which is the
    /// earlier one-at-a-time behaviour. Set the environment variable OMNIWIN_PARALLEL_DESKTOP=1 to turn them on.
    /// </summary>
    private readonly bool _parallelPatternActs = Environment.GetEnvironmentVariable("OMNIWIN_PARALLEL_DESKTOP") == "1";

    /// <summary>True when an act must wait for the single physical lane.</summary>
    internal static bool UsesPhysicalLane(bool usesSharedInput, bool parallelPatternActs) => usesSharedInput || !parallelPatternActs;
    private readonly TimeSpan _queryBudget;
    private readonly Func<Interop.UIAutomationClient.IUIAutomation, IDesktopBackend> _createBackend;
    private IDesktopBackend? _desktop; // Owned by _worker, including initialization.
    private int _disposed;

    public DesktopEngine(OmniWinConfig config, ISafetyGate gate, IKillSwitch killSwitch, IMetrics metrics,
        ILogger<DesktopEngine> log, IUserActivity? activity = null, IOcrRefResolver? ocr = null, IWindowTextReader? screenText = null)
        : this(config, gate, killSwitch, metrics, log, activity, ocr, backend: null, screenText)
    {
    }

    /// <summary>For tests: the engine runs against a fake backend, so no window is touched and no input is sent.</summary>
    internal DesktopEngine(OmniWinConfig config, ISafetyGate gate, IKillSwitch killSwitch, IMetrics metrics,
        ILogger<DesktopEngine> log, IDesktopBackend backend, IUserActivity? activity = null)
        : this(config, gate, killSwitch, metrics, log, activity, null, backend, null)
    {
    }

    private DesktopEngine(OmniWinConfig config, ISafetyGate gate, IKillSwitch killSwitch, IMetrics metrics,
        ILogger<DesktopEngine> log, IUserActivity? activity, IOcrRefResolver? ocr, IDesktopBackend? backend,
        IWindowTextReader? screenText)
    {
        _config = config;
        _gate = gate;
        _killSwitch = killSwitch;
        _metrics = metrics;
        _log = log;
        _activity = activity;
        _ocr = ocr;
        _screenText = screenText;
        _queryBudget = TimeSpan.FromMilliseconds((long)config.Timeouts.UiaConnectionMs + 2L * config.Timeouts.UiaTransactionMs);
        var real = backend is null;
        _worker = new UiaWorker("uia-worker", config.Timeouts, _ => _desktop?.Dispose(), createAutomation: real);
        _events = real ? new UiaEvents(config.Timeouts) : new UiaEvents(config.Timeouts, registerEvents: false, createAutomation: false);
        _createBackend = backend is null ? uia => new UiaDesktop(uia, _events, _config, _ocr, _screenText) : _ => backend;
    }

    public string Component => "desktop";
    public string State => _worker.State;
    public string? Detail => "Windows UI Automation and checked native input";
    internal Action<IReadOnlyDictionary<string, DiagnosticTiming>>? DiagnosticSink { get; set; }

    public async Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var windows = await RunAsync((desktop, token) => desktop.ListWindows(token), ct).ConfigureAwait(false);
        _metrics.Record("windows_list", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return windows;
    }

    public async Task<Observation> ObserveAsync(DesktopObserveRequest req, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var observation = await RunAsync((desktop, token) => desktop.Observe(req, token), ct).ConfigureAwait(false);
        _metrics.Record("desktop_observe", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return observation;
    }

    public Task<ActResult> ActAsync(DesktopActRequest req, CancellationToken ct) =>
        ExecuteAsync(new DesktopCommand("desktop_act", req.Action, req.Ref, req.Generation, Value: req.Value, Expect: req.Expect), ct);

    public Task<ActResult> SendKeysAsync(KeysRequest req, CancellationToken ct) =>
        ExecuteAsync(new DesktopCommand("desktop_keys", "keys", Window: req.Window, Keys: KeySequence.Parse(req.Keys), Expect: req.Expect), ct);

    public Task<ActResult> ScrollAsync(ScrollRequest req, CancellationToken ct) =>
        ExecuteAsync(new DesktopCommand("desktop_scroll", req.Direction, req.Ref, Window: req.Window, Amount: req.Amount), ct);

    public async Task<WaitResult> WaitForAsync(Expectation condition, int timeoutMs, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Math.Clamp(timeoutMs, 1, ExpectSpec.MaxTimeoutMs));
        try
        {
            while (true)
            {
                var stamp = _events.Version;
                var check = await RunAsync((desktop, token) => desktop.Evaluate(condition, null, null, token), deadline.Token).ConfigureAwait(false);
                if (check.Met)
                {
                    return new WaitResult(true, null, null, Timing.Total(Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                }

                await _events.WaitForChangeAsync(stamp, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WaitResult(false, null, null, Timing.Total(Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }
        catch (DesktopFault ex)
        {
            return new WaitResult(false, ex.Reason, null, Timing.Total(Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }
    }

    /// <summary>
    /// Shares the input lane with every other OmniWin process in this Windows session (contract 2.6). The server calls it once at
    /// start-up; tests leave it off so engines in one test process stay independent.
    /// </summary>
    public void UseMachineLane() => _lanes.Machine ??= MachineLane.ForThisSession();

    public Task<IDisposable?> TryEnterAsync(string label, CancellationToken ct) =>
        Task.FromResult<IDisposable?>(_lanes.TryEnterPhysical(label));

    public Task<LabelSet?> LabelsAsync(string windowId, CancellationToken ct) =>
        RunAsync((desktop, token) => desktop.Labels(windowId), ct);

    public Task<bool?> FieldIsEmptyAsync(string elementRef, CancellationToken ct) =>
        RunAsync((desktop, token) => desktop.FieldIsEmpty(elementRef, token), ct);

    public Task<SelectionRead> ReadSelectionAsync(int beforeCaretChars, CancellationToken ct) =>
        RunAsync((desktop, token) => desktop.ReadSelection(beforeCaretChars, token), ct);

    public Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct) =>
        RunAsync((desktop, token) => desktop.DescribeRef(elementRef, token), ct);

    public Task<ResolvedRef?> ResolveSelectorAsync(string process, string windowTitlePattern, IReadOnlyList<SelectorSegment> path,
        CancellationToken ct) => RunAsync((desktop, token) => desktop.ResolveSelector(process, windowTitlePattern, path, token), ct);

    public async Task<bool> BringToForegroundAsync(long hwnd, CancellationToken ct) =>
        (await BringToForegroundDetailedAsync(hwnd, ct).ConfigureAwait(false)).Verified;

    public async Task<ForegroundAttempt> BringToForegroundDetailedAsync(long hwnd, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _killSwitch.Token);
        if (StopReason() is { } stopped)
        {
            return new ForegroundAttempt(false, false, false,
                stopped == ReasonCodes.KillSwitch ? ActStatus.Denied : ActStatus.Blocked, stopped,
                stopped == ReasonCodes.KillSwitch ? "OmniWin is stopped." : "Recent user input paused automation.");
        }

        // A long type holds the lane for up to two minutes: answer at once (nothing was sent) instead of waiting behind it.
        var taken = _lanes.TryEnterPhysical();
        if (taken is null && _lanes.PhysicalHolder is { } holder)
        {
            return new ForegroundAttempt(false, false, false, ActStatus.Blocked, ReasonCodes.RateLimit, ActLanes.BusyDetail(holder));
        }
        using var lease = taken ?? await _lanes.EnterPhysicalAsync(stop.Token).ConfigureAwait(false);
        try
        {
            return await RunAsync((desktop, token) =>
            {
                try
                {
                    var verified = desktop.BringToForeground(hwnd, () => StopReason(), token);
                    var (inputSent, keyTapped) = desktop.LastForegroundAttempt;
                    return new ForegroundAttempt(verified, inputSent, keyTapped);
                }
                catch (DesktopFault stop)
                {
                    // The attempt stopped partway (a boundary check threw). What it recorded before the stop is reported as it
                    // is (also an F24 tap made before the stop), together with the stop's own status and reason.
                    var (inputSent, keyTapped) = desktop.LastForegroundAttempt;
                    return new ForegroundAttempt(false, inputSent, keyTapped, stop.Status, stop.Reason, stop.Message);
                }
            }, stop.Token, mutating: true).ConfigureAwait(false);
        }
        catch (DesktopFault)
        {
            // The attempt stopped partway: what it sent is not known, so it is reported as possibly sent.
            return new ForegroundAttempt(false, true, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _events.DisposeAsync().ConfigureAwait(false);
        await _worker.DisposeAsync().ConfigureAwait(false);
        _lanes.Dispose();
    }

    private async Task<ActResult> ExecuteAsync(DesktopCommand command, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var phaseStart = start;
        var phase = "resolve";
        var phases = new Dictionary<string, double>(StringComparer.Ordinal);
        var progress = new InputProgress();
        PreparedInput? prepared = null;
        ActLanes.Lease? lease = null;
        var userConfirmed = false;
        RiskClass? gateRisk = null;   // Risky when the gate allowed a delete under safety.allow_delete: label only
        var diagnosticSink = DiagnosticSink;
        var diagnostics = diagnosticSink is null ? null : new DesktopDiagnostics.Capture();
        bool UsesSharedInputNow() => command.UsesSharedInput ||
            prepared is not null && NativeDesktop.Foreground == prepared.Hwnd;
        string? CurrentStopReason() => StopReason(UsesSharedInputNow());

        void EndPhase()
        {
            var elapsed = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            // A type spends its whole input phase sending characters (each is sent, then waited for): all of it is dispatch time.
            if (phase == "input" && command.Action != "type")
            {
                var precheck = Math.Max(0, elapsed - progress.DispatchMs);
                phases["precheck"] = phases.GetValueOrDefault("precheck") + precheck;
                elapsed = progress.DispatchMs;
            }

            phases[phase] = phases.GetValueOrDefault(phase) + elapsed;
        }

        void BeginPhase(string next)
        {
            EndPhase();
            phase = next;
            phaseStart = Stopwatch.GetTimestamp();
        }

        ActResult Result(ActStatus status, string? reason = null, string? detail = null, Observation? after = null, string? expect = null)
        {
            EndPhase();
            var total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            foreach (var (name, elapsed) in phases)
            {
                _metrics.Record($"desktop.{name}", elapsed);
            }

            if (progress.Channel is { } channel)
            {
                _metrics.Record($"desktop.input.{channel}", progress.DispatchMs);
            }

            _metrics.Record(command.Tool, total);
            if (diagnostics is not null)
            {
                try
                {
                    diagnosticSink!(diagnostics.Snapshot());
                }
                catch (Exception ex)
                {
                    // A diagnostic consumer must not change whether completed input is reported.
                    _log.LogWarning("Desktop diagnostic callback failed with {Type}.", ex.GetType().Name);
                }
            }
            var result = CreateResult(status, progress, new Timing(total, phases), reason, detail, after, expect);
            return gateRisk is { } risk ? result with { Risk = risk } : result;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _killSwitch.Token);
        CancellationTokenSource? dispatchBudget = null;
        var dispatchLimit = _queryBudget;
        // The typing time limit ran out (not a stop, not the caller): reported as engine_timeout, not as "cancelled".
        bool TypingLimitReached() => dispatchBudget is { IsCancellationRequested: true } && !stop.IsCancellationRequested;
        DesktopFault Interrupted() => TypingLimitReached()
            ? new DesktopFault(ActStatus.Error, ReasonCodes.EngineTimeout,
                $"Stopped after {progress.Sent} input events (the typing time limit of {dispatchLimit.TotalSeconds:0.#} s ran out).")
            : new DesktopFault(ActStatus.Error, ReasonCodes.InputInterrupted,
                $"Stopped after {progress.Sent} input events ({CurrentStopReason() ?? (stop.IsCancellationRequested ? "cancelled" : "engine_timeout")}).");
        try
        {
            CheckStop(UsesSharedInputNow());
            Validate(command);
            if (UsesPhysicalLane(command.UsesSharedInput, _parallelPatternActs))
            {
                // Real mouse and keyboard input: strictly one at a time. (With parallel lanes off, pattern acts also wait here.)
                // A long type holds this lane for a long time; an act that arrives meanwhile is told so at once instead of
                // waiting behind it until its own time limit runs out.
                var label = command.Action == "type" ? ActLanes.TypeLabel : null;
                lease = _lanes.TryEnterPhysical(label);
                if (lease is null && _lanes.PhysicalHolder is { } holder)
                {
                    throw new DesktopFault(ActStatus.Blocked, ReasonCodes.RateLimit, ActLanes.BusyDetail(holder));
                }

                lease ??= await _lanes.EnterPhysicalAsync(stop.Token, label).ConfigureAwait(false);
            }
            else
            {
                // UI Automation pattern act: only acts on the same window wait for each other.
                var windowKey = await RunAsync((desktop, token) => desktop.WindowIdOfRef(command.Ref), stop.Token).ConfigureAwait(false);
                lease = await _lanes.EnterWindowAsync(windowKey ?? UnresolvedWindowKey, stop.Token).ConfigureAwait(false);
            }

            CheckStop(UsesSharedInputNow());

            BeginPhase("precheck");
            prepared = await RunAsync((desktop, token) =>
            {
                using var diagnostic = DesktopDiagnostics.Activate(diagnostics, "prepare");
                return desktop.Prepare(command, null, token);
            }, stop.Token).ConfigureAwait(false);

            // A physical act now knows its window: no other act may work on that window until this one ends.
            // (Pattern acts took this lock before Prepare. A pattern act that resolved to a different window than its
            // ref said cannot happen: the window comes from the ref.)
            if (lease.HoldsPhysical)
            {
                await lease.EnterWindowAsync(prepared.Window, stop.Token).ConfigureAwait(false);
            }
            BeginPhase("gate");
            var gate = await _gate.AuthorizeAsync(prepared.Descriptor, stop.Token).WaitAsync(stop.Token).ConfigureAwait(false);
            if (!gate.Allowed)
            {
                return Result(ActStatus.Denied, gate.Reason ?? ReasonCodes.PolicyForbidden, gate.Detail);
            }
            userConfirmed = gate.UserConfirmed;
            if (gate.Risk == RiskClass.Risky) gateRisk = RiskClass.Risky;

            // A named background window first needs authorization to activate it. Then check and authorize
            // the actual focused element before sending any keys.
            if (command.Keys is not null && prepared.Identity is null)
            {
                CheckStop(UsesSharedInputNow());
                BeginPhase("precheck");
                var previous = prepared;
                prepared = await RunAsync((desktop, token) =>
                {
                    using var diagnostic = DesktopDiagnostics.Activate(diagnostics, "prepare");
                    return desktop.Prepare(command, previous, token, progress, CurrentStopReason);
                }, stop.Token, mutating: true).ConfigureAwait(false);
                BeginPhase("gate");
                gate = await _gate.AuthorizeAsync(prepared.Descriptor, stop.Token).WaitAsync(stop.Token).ConfigureAwait(false);
                if (!gate.Allowed)
                {
                    return Result(ActStatus.Denied, gate.Reason ?? ReasonCodes.PolicyForbidden, gate.Detail);
                }
                userConfirmed |= gate.UserConfirmed;
                if (gate.Risk == RiskClass.Risky) gateRisk = RiskClass.Risky;
            }

            CheckStop(UsesSharedInputNow());
            BeginPhase("input");
            var target = prepared;
            dispatchBudget = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            dispatchLimit = command.Action == "type" ? TypingBudget(_queryBudget, command.Value?.Length ?? 0) : _queryBudget;
            dispatchBudget.CancelAfter(dispatchLimit);
            // The typing limit covers the whole act. The first call does the set-up and the first character (it may need the
            // whole limit for a large document); every later call is one character, so the worker is free in between.
            await RunAsync((desktop, token) =>
            {
                using var diagnostic = DesktopDiagnostics.Activate(diagnostics, "dispatch");
                try
                {
                    desktop.Dispatch(command, target, progress, CurrentStopReason, token);
                }
                catch (OperationCanceledException) when (progress.Sent > 0)
                {
                    throw Interrupted();
                }

                return true;
            }, dispatchBudget.Token, mutating: true, budget: dispatchLimit).ConfigureAwait(false);

            while (progress.TypingPending)
            {
                // Bounded polling for native editor acknowledgement. Yield outside the MTA so
                // its UI thread can process the queued scalar; every resume rechecks boundaries.
                var waitStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                if (progress.TypingPolls == 0 || Stopwatch.GetElapsedTime(progress.TypingSentAt).TotalMilliseconds < 2) await Task.Yield();
                else await Task.Delay(1, dispatchBudget.Token).ConfigureAwait(false);
                diagnostics?.Record("dispatch", "RichEdit.PollWait", waitStarted);
                await RunAsync((desktop, token) =>
                {
                    using var diagnostic = DesktopDiagnostics.Activate(diagnostics, "dispatch");
                    try
                    {
                        desktop.ContinueTyping(progress, token);
                    }
                    catch (OperationCanceledException) when (progress.Sent > 0)
                    {
                        throw Interrupted();
                    }
                    return true;
                }, dispatchBudget.Token, mutating: true).ConfigureAwait(false);
            }

            BeginPhase("verify");
            var verification = await VerifyAsync(command, prepared, progress, stop.Token).ConfigureAwait(false);
            return Result(verification.Status, verification.Reason, verification.Detail, verification.After, verification.Outcome);
        }
        catch (DesktopFault ex)
        {
            if (progress.Sent > 0 && ex.Status is ActStatus.Blocked or ActStatus.Denied or ActStatus.Stale)
            {
                return Result(ActStatus.Error, ReasonCodes.InputInterrupted,
                    $"Stopped after {progress.Sent} input events ({ex.Reason}). {ex.Message}");
            }

            var status = progress.Sent == 0 && userConfirmed && ex.Status == ActStatus.Stale ? ActStatus.Blocked : ex.Status;
            var after = ex.WindowId is { } window
                ? await TryReadAfterAsync(window, prepared?.Baseline, command.Generation, ct).ConfigureAwait(false) : null;
            return Result(status, ex.Reason, ex.Message, after);
        }
        catch (OperationCanceledException)
        {
            if (progress.Sent > 0 && phase == "verify")
            {
                return Result(ActStatus.Error, detail: "Verification was cancelled after input completed. Observe before retrying.");
            }
            if (progress.Sent > 0)
            {
                var interrupted = Interrupted();
                return Result(interrupted.Status, interrupted.Reason, interrupted.Message);
            }

            return CurrentStopReason() == ReasonCodes.KillSwitch
                ? Result(ActStatus.Denied, ReasonCodes.KillSwitch, "OmniWin is stopped.")
                : Result(ActStatus.Error, ReasonCodes.EngineTimeout, "The operation was cancelled before input.");
        }
        catch (Exception ex)
        {
            _log.LogWarning("Desktop operation {Tool} failed with {Type} (0x{HResult:X8}).", command.Tool, ex.GetType().Name, ex.HResult);
            return Result(ActStatus.Error, detail: progress.Sent > 0
                ? "The provider failed after input dispatch. Input may have been delivered. Observe before retrying."
                : "Windows could not complete the desktop operation before input.");
        }
        finally
        {
            try
            {
                if (progress.TypingPending)
                {
                    try
                    {
                        // Managed cleanup only: discard the worker-owned continuation, never resume input.
                        await _worker.InvokeAsync((_, _) =>
                        {
                            _desktop?.CancelTyping(progress);
                            return true;
                        }, CancellationToken.None).WaitAsync(_queryBudget).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is System.Threading.Channels.ChannelClosedException or ObjectDisposedException)
                    {
                        // Shutdown also releases the worker and its managed continuation.
                    }
                    catch (TimeoutException)
                    {
                        // A stuck read must not hold the input lock indefinitely during cancellation.
                        // This queued cleanup is managed-only and owner-matched, so it can safely run later.
                    }
                }
            }
            finally
            {
                lease?.Dispose();
                dispatchBudget?.Dispose();
            }
        }
    }

    internal static ActResult CreateResult(ActStatus status, InputProgress progress, Timing timing,
        string? reason = null, string? detail = null, Observation? after = null, string? expect = null)
    {
        if (progress.Sent > 0 && status is ActStatus.Denied or ActStatus.Blocked or ActStatus.Stale)
        {
            detail = $"The action stopped after a focus or input operation ({reason}). {detail}";
            status = ActStatus.Error;
            reason = ReasonCodes.InputInterrupted;
        }

        return new ActResult(status, progress.Sent > 0, reason, detail, after, progress.Channel, timing,
            ExpectOutcome: expect);
    }

    private async Task<Verification> VerifyAsync(DesktopCommand command, PreparedInput prepared, InputProgress progress, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(command.Expect?.EffectiveTimeoutMs ?? 300);
        ConditionCheck? check = null;
        Observation? after = null;
        try
        {
            while (true)
            {
                var stamp = _events.Version;
                if (command.Expect is { } expectation)
                {
                    check = await RunAsync((desktop, token) =>
                        desktop.Evaluate(expectation.Condition, prepared.Window, progress.OpenWindows, token), deadline.Token).ConfigureAwait(false);
                }
                else
                {
                    check = await RunAsync((desktop, token) =>
                        desktop.EvaluateDefault(command, prepared, progress, token), deadline.Token).ConfigureAwait(false);
                    if (check is null)
                    {
                        after = await RunAsync((desktop, token) =>
                            desktop.ReadAfter(prepared.Window, prepared.Baseline, null, token), deadline.Token).ConfigureAwait(false);
                        if (after is not null && after.Generation != prepared.Baseline?.Generation) break;
                    }
                }

                if (check?.Met == true) break;
                await _events.WaitForChangeAsync(stamp, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // An expectation deadline ends verification, not the input that was already sent.
        }

        after ??= await TryReadAfterAsync(prepared.Window, prepared.Baseline, null, ct).ConfigureAwait(false);
        if (check?.Met == true && ValueSettingPolicy.MustNotConfirm(command.Action, prepared.Descriptor.Target))
        {
            // D1: a matching readback on a file dialog's File name box or Explorer's address bar does not prove the app used the value.
            return new Verification(ActStatus.Unverified, null, ValueSettingPolicy.IgnoredDetail, after, check.Outcome);
        }

        if (check?.Met == true && progress.Resends > 0 && command.Action == "type")
        {
            // A1/A8: a character was sent twice in this act. The late original can still land after a matching readback, so
            // "confirmed" needs a readback of text that stayed unchanged for 500 ms (read about every 100 ms, at most 2 s).
            var settled = await SettledTypeCheckAsync(command, prepared, progress, ct).ConfigureAwait(false);
            if (ResendOutcome(settled) is { } downgrade)
            {
                return new Verification(downgrade.Status, downgrade.Reason, downgrade.Detail,
                    downgrade.Status == ActStatus.Contradicted
                        ? await TryReadAfterAsync(prepared.Window, prepared.Baseline, null, ct).ConfigureAwait(false)
                        : after,
                    settled?.Mismatch == true ? settled.Outcome : check.Outcome);
            }
        }

        if (check?.Met == true) return new Verification(ActStatus.Confirmed, null, null, after, check.Outcome);
        if (check?.Mismatch == true)
        {
            return new Verification(ActStatus.Contradicted, ReasonCodes.ReadbackMismatch,
                "The control did not report the expected state.", after, check.Outcome);
        }

        return command.Expect is not null
            ? new Verification(ActStatus.Timeout, null, "The expectation was not met before its deadline.", after, check?.Outcome)
            : new Verification(ActStatus.Unverified, null,
                after is null || after.Generation == prepared.Baseline?.Generation ? "No visible change." : null, after, null);
    }

    /// <summary>A8: the text must have stayed unchanged for this long before a re-sent character's act can be confirmed.</summary>
    internal const int ResendSettleMs = 500;
    internal const int ResendPollMs = 100;
    internal const int ResendSettleCapMs = 2000;

    /// <summary>
    /// A8: reads the text back until it has stayed the same for <paramref name="stableMs"/> (read every <paramref name="pollMs"/>),
    /// giving up after <paramref name="capMs"/>. The check that comes back is the stable one: met (the text is exactly what was
    /// requested), mismatch (it is something else), or null (it never became stable, or could not be read). A text that changes
    /// between readings restarts the 500 ms. Pure apart from the delay, so it is tested without a window.
    /// </summary>
    internal static async Task<ConditionCheck?> StableReadbackAsync(Func<CancellationToken, Task<ConditionCheck?>> read,
        int stableMs, int pollMs, int capMs, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var state = -1;   // 0 unreadable, 1 matches, 2 differs
        long since = 0;
        ConditionCheck? last = null;
        while (true)
        {
            var check = await read(ct).ConfigureAwait(false);
            var now = clock.ElapsedMilliseconds;
            var current = check is null || !check.Met && !check.Mismatch ? 0 : check.Met ? 1 : 2;
            if (current != state)
            {
                state = current;
                since = now;
            }

            last = check;
            if (current != 0 && now - since >= stableMs) return check;
            if (now >= capMs) break;
            await Task.Delay((int)Math.Min(pollMs, capMs - now), ct).ConfigureAwait(false);
        }

        // The cap ran out. A text that is not what was requested is reported as such; anything else is not proven.
        return state == 2 ? last : null;
    }

    /// <summary>
    /// A1: what a type action that re-sent a character may report once the text had settled. Null means the settled readback
    /// still matches exactly, so "confirmed" stands; a changed text is contradicted; an unreadable text is unverified.
    /// </summary>
    internal static (ActStatus Status, string? Reason, string Detail)? ResendOutcome(ConditionCheck? settled)
    {
        if (settled?.Mismatch == true)
            return (ActStatus.Contradicted, ReasonCodes.ReadbackMismatch,
                "A character was sent again and the text then changed: it is not what was requested.");
        if (settled?.Met != true)
            return (ActStatus.Unverified, null, "A character was sent again and the text could not be read back after it settled.");
        return null;
    }

    /// <summary>The default text readback of a type action once the text has stayed unchanged for 500 ms (A8). Null when it never became stable.</summary>
    private async Task<ConditionCheck?> SettledTypeCheckAsync(DesktopCommand command, PreparedInput prepared, InputProgress progress, CancellationToken ct)
    {
        async Task<ConditionCheck?> Read(CancellationToken token)
        {
            try
            {
                return await RunAsync((desktop, t) => desktop.EvaluateDefault(command, prepared, progress, t), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DesktopFault or COMException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                return null;
            }
        }

        return await StableReadbackAsync(Read, ResendSettleMs, ResendPollMs, ResendSettleCapMs, ct).ConfigureAwait(false);
    }

    private async Task<Observation?> TryReadAfterAsync(string window, DesktopSnapshot? baseline, long? since, CancellationToken ct)
    {
        try
        {
            return await RunAsync((desktop, token) => desktop.ReadAfter(window, baseline, since, token), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DesktopFault or COMException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Per-character allowance and hard upper bound for one type action (A3).</summary>
    internal const int TypingMsPerCharacter = 60;
    internal static readonly TimeSpan TypingBudgetCap = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long one type action may take to send its text: the normal budget plus 60 ms for every character (each character
    /// is acknowledged before the next), never more than 120 s. Long text finishes; nothing waits forever.
    /// </summary>
    internal static TimeSpan TypingBudget(TimeSpan baseBudget, int characters)
    {
        var scaled = baseBudget + TimeSpan.FromMilliseconds((long)TypingMsPerCharacter * Math.Max(0, characters));
        var cap = baseBudget > TypingBudgetCap ? baseBudget : TypingBudgetCap;
        return scaled < cap ? scaled : cap;
    }

    private async Task<T> RunAsync<T>(Func<IDesktopBackend, CancellationToken, T> command, CancellationToken ct, bool mutating = false,
        TimeSpan? budget = null)
    {
        using var budgetSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetSource.CancelAfter(budget ?? _queryBudget);
        try
        {
            var work = _worker.InvokeAsync((uia, token) =>
                command(_desktop ??= _createBackend(uia), token), budgetSource.Token);
            return mutating ? await work.ConfigureAwait(false) : await work.WaitAsync(budgetSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DesktopFault(ActStatus.Error, ReasonCodes.EngineTimeout, "Windows UI Automation exceeded its transaction budget.");
        }
        catch (COMException ex) when (ex.HResult is unchecked((int)0x80131505) or unchecked((int)0x800705B4) or unchecked((int)0x8001011F))
        {
            throw new DesktopFault(ActStatus.Error, ReasonCodes.EngineTimeout, "Windows UI Automation timed out.");
        }
    }

    // Pattern acts whose ref no longer resolves share one gate; Prepare then answers stale/not_found quickly.
    private const string UnresolvedWindowKey = "?";

    private string? StopReason(bool sharedInput = true) => _killSwitch.IsTripped ? ReasonCodes.KillSwitch :
        sharedInput && _activity?.UserActiveRecently == true ? ReasonCodes.UserActive : null;

    private void CheckStop(bool sharedInput = true)
    {
        var reason = StopReason(sharedInput);
        if (reason is not null)
        {
            throw new DesktopFault(reason == ReasonCodes.KillSwitch ? ActStatus.Denied : ActStatus.Blocked, reason,
                reason == ReasonCodes.KillSwitch ? "OmniWin is stopped." : "Recent user input paused automation.");
        }
    }

    private static void Validate(DesktopCommand command)
    {
        var valid = command.Tool switch
        {
            "desktop_act" => !string.IsNullOrWhiteSpace(command.Ref) && command.Generation >= 0 &&
                (command.Action switch
                {
                    "type" => command.Value is { Length: > 0 } text && !text.Contains('\0'),
                    "set_value" => command.Value is { } value && !value.Contains('\0'),
                    "click" or "double_click" or "right_click" or "invoke" or "toggle" or "select" or "expand" or "collapse" or
                        "clear" or "focus" or "scroll_into_view" or "hover" => command.Value is null,
                    // drag: Value is the ref of the drop target (an element ref, like e30).
                    "drag" => command.Value is { Length: > 1 } dropRef && dropRef[0] == 'e' && dropRef.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0,
                    _ => false,
                }),
            "desktop_keys" => command.Keys is not null,
            "desktop_scroll" => command.Action is "up" or "down" or "left" or "right" && command.Amount is > 0 and <= int.MaxValue / 120,
            _ => false,
        };
        if (!valid)
        {
            throw new DesktopFault(ActStatus.Blocked, ReasonCodes.UnsupportedAction, "The action or its arguments are invalid.");
        }
    }

    private sealed record Verification(ActStatus Status, string? Reason, string? Detail, Observation? After, string? Outcome);
}
