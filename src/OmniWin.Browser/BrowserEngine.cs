using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser;

public sealed class BrowserEngine : IBrowserEngine, IBrowserViewer, IComponentStatus, IAsyncDisposable
{
    private readonly OmniWinConfig _config;
    private readonly OmniWinPaths _paths;
    private readonly ISafetyGate _safetyGate;
    private readonly IKillSwitch _killSwitch;
    private readonly IUserActivity _userActivity;
    private readonly IMetrics _metrics;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private BrowserLauncher? _launcher;
    private CdpConnection? _connection;
    internal CdpConnection? Connection => _connection;

    /// <summary>
    /// Test hook: runs on every new CDP connection, before its first command is sent, including after a reconnect.
    /// Tests use it to install <c>NavigationGuard</c> so no connection can ever run without it.
    /// </summary>
    internal Action<CdpConnection>? ConnectionCreated { get; set; }

    public string BrowserProcessName =>
        _launcher?.BrowserProcessName ?? Path.GetFileName(BrowserLauncher.FindBrowserExecutable(_config.Browser.Channel));
    private readonly ConcurrentDictionary<string, CdpSession> _sessions = new(); // sessionId -> session
    private readonly ConcurrentDictionary<string, string> _targetToTab = new(); // targetId -> tabId
    private readonly ConcurrentDictionary<string, string> _tabToTarget = new(); // tabId -> targetId
    private readonly ConcurrentDictionary<string, ElementScanResult> _lastScans = new(); // tabId -> scan
    private readonly ConcurrentDictionary<string, Dictionary<string, ElementNodeData>> _cachedElements = new(); // tabId -> (bN -> data)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, Dictionary<string, ElementNodeData>>> _history = new(); // tabId -> (gen -> elements)

    private TaskCompletionSource _firstSessionAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _nextTabId = 1;
    // Parallel lanes: every tab hands out ref numbers from its own residue class (lane + 1, lane + 1 + Lanes, ...), so refs are
    // unique across tabs and a ref identifies its tab. Tabs that share a lane (more than Lanes tabs) take turns scanning.
    private const int Lanes = 16;
    private readonly SemaphoreSlim[] _laneGates = Enumerable.Range(0, Lanes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly int[] _laneNext = Enumerable.Range(1, Lanes).ToArray();
    private readonly ConcurrentDictionary<string, string> _refTabs = new(); // bN -> tabId
    private volatile string? _activeTabId;
    private bool _isDisposed;

    // Owner decision 2026-11-08: nothing is blocked by policy except deleting. A navigation, link click or key press is
    // never refused for its URL scheme; IsNonWebScheme / ClassifyNavRisk / ClassifyActRisk only feed the Risky label.

    private static int HistoryGenerationsToKeep => 8;

    /// <summary>
    /// Records one observation. Generations only go up within a document and restart at 1 on a new document, so a
    /// generation that is not above the newest recorded one means the page was replaced: the old entries are dropped
    /// (their refs are gone with the old document) before the new one is stored and the history is trimmed.
    /// </summary>
    private static void RecordHistory(
        ConcurrentDictionary<long, Dictionary<string, ElementNodeData>> tabHistory,
        long generation,
        Dictionary<string, ElementNodeData> elements)
    {
        lock (tabHistory)
        {
            if (!tabHistory.IsEmpty && generation <= tabHistory.Keys.Max())
            {
                tabHistory.Clear();
            }

            tabHistory[generation] = elements;
            TrimHistory(tabHistory);
        }
    }

    private static void TrimHistory(ConcurrentDictionary<long, Dictionary<string, ElementNodeData>> tabHistory)
    {
        if (tabHistory.Count <= HistoryGenerationsToKeep) return;
        foreach (var gen in tabHistory.Keys.OrderByDescending(g => g).Skip(HistoryGenerationsToKeep).ToList())
        {
            tabHistory.TryRemove(gen, out _);
        }
    }

    private static bool IsActivationKey(string? key) =>
        string.IsNullOrEmpty(key) ||
        key.Equals("enter", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("return", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("space", StringComparison.OrdinalIgnoreCase) ||
        key == " ";

    private static string MapPageFailure(string? pageReason) => pageReason switch
    {
        "not_found" => ReasonCodes.NotFound,
        "focus_failed" => ReasonCodes.FocusFailed,
        _ => ReasonCodes.InputInterrupted
    };

    public static bool IsAllowedWebScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (string.Equals(trimmed, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNonWebScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (string.Equals(trimmed, "about:blank", StringComparison.OrdinalIgnoreCase)) return false;
        return !IsAllowedWebScheme(trimmed);
    }

    public static RiskClass ClassifyNavRisk(string action, string? url)
    {
        if (string.Equals(action, "close_tab", StringComparison.OrdinalIgnoreCase)) return RiskClass.Risky;
        if (IsNonWebScheme(url)) return RiskClass.Risky;
        return RiskClass.Normal;
    }

    public static RiskClass ClassifyActRisk(string action, LiveElementInspection live, string? value = null)
    {
        if ((action is "click" or "double_click" || (action == "press" && IsActivationKey(value))) &&
            live.Role == "link" && !string.IsNullOrEmpty(live.Value) && IsNonWebScheme(live.Value))
        {
            return RiskClass.Risky;
        }

        if (action == "press" && (string.Equals(value, "enter", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "return", StringComparison.OrdinalIgnoreCase)))
        {
            if (live.IsPassword) return RiskClass.Risky;
        }

        return RiskClass.Normal;
    }

    public static string ComputeExpectedTypeValue(string? original, int? selectionStart, int? selectionEnd, string insertion)
    {
        if (string.IsNullOrEmpty(original))
        {
            return insertion;
        }

        if (selectionStart is int start && selectionEnd is int end &&
            start >= 0 && end >= start && end <= original.Length)
        {
            return string.Concat(original.AsSpan(0, start), insertion.AsSpan(), original.AsSpan(end));
        }

        return original + insertion;
    }

    public BrowserEngine(
        OmniWinConfig config,
        OmniWinPaths paths,
        ISafetyGate safetyGate,
        IKillSwitch killSwitch,
        IUserActivity userActivity,
        IMetrics metrics)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _userActivity = userActivity ?? throw new ArgumentNullException(nameof(userActivity));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public string Component => "browser";
    public string State => (_connection is { IsConnected: true } && _launcher is { IsRunning: true }) ? "attached" : "detached";
    public string? Detail => _launcher?.ProfileDir is not null ? $"dedicated profile at {_launcher.ProfileDir}" : "not running";

    public async Task EnsureConnectedAsync(bool? headless = null, CancellationToken ct = default)
    {
        if (_connection is { IsConnected: true } && _launcher is { IsRunning: true })
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsConnected: true } && _launcher is { IsRunning: true })
            {
                return;
            }

            // OMNIWIN_BROWSER_HEADLESS=1 starts the browser without a window (used by the end-to-end test harness).
            headless ??= Environment.GetEnvironmentVariable("OMNIWIN_BROWSER_HEADLESS") == "1" ? true : null;

            // Reconnecting after a dropped connection or a closed browser: close what is left of the previous launch first, so a
            // half-alive browser on the same profile can neither stall the new launch nor be left behind.
            if (_connection is not null)
            {
                try { await _connection.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
                _connection = null;
            }

            if (_launcher is not null)
            {
                try { await _launcher.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
                _launcher = null;
            }

            _launcher = new BrowserLauncher(_config, _paths);
            var wsUrl = await _launcher.LaunchAsync(headless, ct).ConfigureAwait(false);

            _firstSessionAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _connection = new CdpConnection();
            ConnectionCreated?.Invoke(_connection);
            _connection.EventReceived += OnCdpEventReceivedAsync;
            _connection.Disconnected += OnCdpDisconnected;
            await _connection.ConnectAsync(wsUrl, ct).ConfigureAwait(false);

            // Enable target auto-attach
            await _connection.SendAsync("Target.setDiscoverTargets", new { discover = true }, null, ct).ConfigureAwait(false);
            await _connection.SendAsync("Target.setAutoAttach", new
            {
                autoAttach = true,
                waitForDebuggerOnStart = false,
                flatten = true
            }, null, ct).ConfigureAwait(false);

            // If the browser has no page yet, create one. Either way, wait for the attach event
            // instead of sleeping a fixed time.
            var existing = await _connection.SendAsync("Target.getTargets", null, null, ct).ConfigureAwait(false);
            var hasPage = existing.TryGetProperty("targetInfos", out var infos) &&
                          infos.ValueKind == JsonValueKind.Array &&
                          infos.EnumerateArray().Any(t => t.TryGetProperty("type", out var tp) && tp.GetString() == "page");

            if (!hasPage)
            {
                var created = await _connection.SendAsync("Target.createTarget", new { url = "about:blank" }, null, ct).ConfigureAwait(false);
                if (created.TryGetProperty("targetId", out var tProp))
                {
                    var targetId = tProp.GetString()!;
                    var tabId = GetOrCreateTabId(targetId);
                    _activeTabId = tabId;
                }
            }

            try
            {
                await _firstSessionAttached.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                var first = _sessions.Values.FirstOrDefault();
                if (first?.Initialization is { } init)
                {
                    await init.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                // Sessions are also attached lazily by later events; callers report browser_not_connected if none exists.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Initialization problems surface on the first action as isolated-world errors.
            }
        }
        catch
        {
            // A launch that fails or is cancelled partway (browser up but CDP not reachable, timeout, cancel) must not leave a
            // hidden browser running: drop the connection and stop this launch's processes by their unique profile folder.
            try { if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _connection = null;
            if (_launcher is not null)
            {
                try { await _launcher.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
                _launcher = null;
            }

            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task OnCdpEventReceivedAsync(CdpEvent e)
    {
        if (e.Method == "Target.attachedToTarget" && e.Params.TryGetProperty("targetInfo", out var tInfo))
        {
            var targetId = tInfo.TryGetProperty("targetId", out var tidProp) ? tidProp.GetString() : null;
            var type = tInfo.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            var sessionId = e.Params.TryGetProperty("sessionId", out var sidProp) ? sidProp.GetString() : null;

            if (type == "page" && targetId is not null && sessionId is not null)
            {
                var tabId = GetOrCreateTabId(targetId);
                var session = new CdpSession(sessionId, targetId, tabId, _connection!) { RefStride = Lanes };
                _sessions[sessionId] = session;
                session.Initialization = session.InitializeAsync();
                _firstSessionAttached.TrySetResult();
                if (_activeTabId is null)
                {
                    _activeTabId = tabId;
                }
            }
        }
        else if (e.Method == "Target.detachedFromTarget" && e.Params.TryGetProperty("sessionId", out var sidProp))
        {
            var sid = sidProp.GetString();
            if (sid is not null && _sessions.TryRemove(sid, out var session))
            {
                _cachedElements.TryRemove(session.TabId, out _);
                _lastScans.TryRemove(session.TabId, out _);
                foreach (var stale in _refTabs.Where(kv => kv.Value == session.TabId).Select(kv => kv.Key).ToList())
                {
                    _refTabs.TryRemove(stale, out _);
                }
            }
        }
        else if (!string.IsNullOrEmpty(e.SessionId) && _sessions.TryGetValue(e.SessionId, out var targetSession))
        {
            targetSession.HandleEvent(e.Method, e.Params);
        }

        return Task.CompletedTask;
    }

    private void OnCdpDisconnected()
    {
        _sessions.Clear();
    }

    private string GetOrCreateTabId(string targetId)
    {
        return _targetToTab.GetOrAdd(targetId, _ =>
        {
            var id = $"t{Interlocked.Increment(ref _nextTabId) - 1}";
            _tabToTarget[id] = targetId;
            return id;
        });
    }

    private CdpSession? GetActiveSession()
    {
        if (_activeTabId is not null)
        {
            foreach (var s in _sessions.Values)
            {
                if (s.TabId == _activeTabId) return s;
            }
        }
        return _sessions.Values.FirstOrDefault();
    }

    /// <summary>The session of the tab a ref was found on, or null when the ref is unknown.</summary>
    private CdpSession? GetSessionForRef(string? elementRef) =>
        elementRef is not null && _refTabs.TryGetValue(elementRef, out var tab) ? GetSessionForTab(tab) : null;

    private static int LaneOf(CdpSession session) =>
        session.TabId.Length > 1 && int.TryParse(session.TabId.AsSpan(1), out var n) && n >= 1 ? (n - 1) % Lanes : 0;

    /// <summary>
    /// Scans a tab. Scans on the same lane run one at a time (so the lane's counter stays consistent); tabs on different lanes
    /// scan in parallel. Records which tab each ref belongs to.
    /// </summary>
    private async Task<ElementScanResult> ScanTabAsync(CdpSession session, int maxElements, bool includeText, bool viewportOnly, CancellationToken ct)
    {
        var lane = LaneOf(session);
        await _laneGates[lane].WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var start = Volatile.Read(ref _laneNext[lane]);
            var scan = await session.ScanElementsAsync(start, maxElements, includeText, viewportOnly, ct).ConfigureAwait(false);
            if (scan.NextRefId > _laneNext[lane])
            {
                Volatile.Write(ref _laneNext[lane], scan.NextRefId);
            }

            foreach (var element in scan.Elements)
            {
                _refTabs[element.Ref] = session.TabId;
            }

            return scan;
        }
        finally
        {
            _laneGates[lane].Release();
        }
    }

    private sealed class TabLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>
    /// Takes the tab's operation gate. Returns null when the stop key was tripped while waiting; a caller cancellation throws.
    /// </summary>
    private async Task<TabLease?> TryEnterTabAsync(CdpSession session, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _killSwitch.Token);
        try
        {
            await session.OpGate.WaitAsync(linked.Token).ConfigureAwait(false);
            return new TabLease(session.OpGate);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _killSwitch.IsTripped)
        {
            return null;
        }
    }

    private CdpSession? GetSessionForTab(string? tabId)
    {
        if (string.IsNullOrEmpty(tabId))
        {
            return GetActiveSession();
        }

        foreach (var s in _sessions.Values)
        {
            if (s.TabId == tabId) return s;
        }

        return null;
    }

    public async Task<IReadOnlyList<TabInfo>> ListTabsAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(null, ct).ConfigureAwait(false);

        var list = new List<TabInfo>();
        var targetsRes = await _connection!.SendAsync("Target.getTargets", null, null, ct).ConfigureAwait(false);

        if (targetsRes.TryGetProperty("targetInfos", out var targetsArray) && targetsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var target in targetsArray.EnumerateArray())
            {
                var type = target.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                if (type != "page") continue;

                var targetId = target.TryGetProperty("targetId", out var tidProp) ? tidProp.GetString()! : string.Empty;
                var url = target.TryGetProperty("url", out var uProp) ? uProp.GetString()! : string.Empty;
                var title = target.TryGetProperty("title", out var titleProp) ? titleProp.GetString()! : string.Empty;

                var tabId = GetOrCreateTabId(targetId);
                var isActive = tabId == _activeTabId;
                list.Add(new TabInfo(tabId, url, title, isActive));
            }
        }

        return list;
    }

    public async Task<ActResult> NavigateAsync(BrowserNavRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await EnsureConnectedAsync(null, ct).ConfigureAwait(false);
        var tabSession = GetSessionForTab(req.Tab);

        // One navigation or act at a time per tab; other tabs are not held up.
        TabLease? lease = null;
        if (tabSession is not null)
        {
            lease = await TryEnterTabAsync(tabSession, ct).ConfigureAwait(false);
            if (lease is null)
            {
                return ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0)) with { Risk = ClassifyNavRisk(req.Action, req.Url) };
            }
        }

        ActResult result;
        using (lease)
        {
            result = await NavigateCoreAsync(req, ct).ConfigureAwait(false);
        }

        // Q6: the Risky label only records. It is set on every result, including denied, blocked and error ones, and
        // never changes the status, the reason or input_sent.
        return result with { Risk = ClassifyNavRisk(req.Action, req.Url) };
    }

    private async Task<ActResult> NavigateCoreAsync(BrowserNavRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var sw = Stopwatch.StartNew();
        var phases = new Dictionary<string, double>();

        await EnsureConnectedAsync(null, ct).ConfigureAwait(false);
        var session = GetSessionForTab(req.Tab);
        if (session is null)
        {
            return ActResult.Failed(false, ReasonCodes.BrowserNotConnected, "Browser tab not found", Timing.Total(sw.Elapsed.TotalMilliseconds));
        }

        if (req.Action is "dialog_accept" or "dialog_dismiss" && !session.HasOpenDialog)
        {
            return ActResult.Blocked(ReasonCodes.NotFound, "No JavaScript dialog is open on this tab", Timing.Total(sw.Elapsed.TotalMilliseconds));
        }

        // Phase 1: Prechecks (Stop key and User Activity)
        if (_killSwitch.IsTripped)
        {
            return ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", new Timing(sw.Elapsed.TotalMilliseconds, phases));
        }

        if (_userActivity.UserActiveRecently)
        {
            return ActResult.Blocked(ReasonCodes.UserActive, "User active recently", new Timing(sw.Elapsed.TotalMilliseconds, phases));
        }

        // Phase 2: Safety Gate
        var swGate = Stopwatch.StartNew();
        var procName = BrowserProcessName;
        // Q8: accepting a dialog goes through the gate with the dialog text as the target name, so accepting a delete
        // confirmation is denied by the existing no-delete rule. Dismissing carries no target and is always allowed.
        var dialogTarget = req.Action == "dialog_accept" && session.OpenDialogMessage is { } dialogText
            ? new ElementDescriptor("dialog", "browser", procName, session.CurrentTitle, session.CurrentUrl, "dialog", dialogText, null, false, true, [])
            : null;
        var desc = new ActionDescriptor(
            "browser_nav",
            "browser",
            req.Action == "dialog_accept" ? "click" : req.Action,   // the delete rule inspects clicks; accepting is a click on OK
            dialogTarget,
            procName,
            session.CurrentTitle,
            req.Url ?? session.CurrentUrl,
            req.Url,
            null);

        var gateRes = await _safetyGate.AuthorizeAsync(desc, ct).ConfigureAwait(false);
        phases["gate"] = swGate.Elapsed.TotalMilliseconds;

        if (!gateRes.Allowed)
        {
            return ActResult.Denied(gateRes.Reason ?? ReasonCodes.PolicyForbidden, gateRes.Detail ?? "Navigation denied by safety policy", new Timing(sw.Elapsed.TotalMilliseconds, phases));
        }

        if (_killSwitch.IsTripped)
        {
            return ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", new Timing(sw.Elapsed.TotalMilliseconds, phases));
        }

        if (_userActivity.UserActiveRecently)
        {
            return ActResult.Blocked(ReasonCodes.UserActive, "User active recently", new Timing(sw.Elapsed.TotalMilliseconds, phases));
        }

        var inputSent = false;
        var swInput = Stopwatch.StartNew();
        try
        {
            switch (req.Action.ToLowerInvariant())
            {
                case "navigate":
                    if (string.IsNullOrEmpty(req.Url))
                    {
                        return ActResult.Failed(false, ReasonCodes.UnsupportedAction, "URL required for navigate", Timing.Total(sw.Elapsed.TotalMilliseconds));
                    }
                    await session.NavigateAsync(req.Url, () => inputSent = true, ct).ConfigureAwait(false);
                    break;

                case "reload":
                    inputSent = true;
                    var reloadNav = session.PrepareForNavigation();
                    await session.SendCommandAsync("Page.reload", null, ct).ConfigureAwait(false);
                    await session.WaitForNavigationAsync(reloadNav, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    break;

                case "back":
                    inputSent = true;
                    var backNav = session.PrepareForNavigation();
                    await session.EvaluateRawAsync("window.history.back()", ct).ConfigureAwait(false);
                    await session.WaitForNavigationAsync(backNav, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    break;

                case "forward":
                    inputSent = true;
                    var fwdNav = session.PrepareForNavigation();
                    await session.EvaluateRawAsync("window.history.forward()", ct).ConfigureAwait(false);
                    await session.WaitForNavigationAsync(fwdNav, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    break;

                case "new_tab":
                    inputSent = true;
                    var newUrl = string.IsNullOrEmpty(req.Url) ? "about:blank" : req.Url;
                    // Parallel lanes: a new tab gets its own window. A tab that is not the front tab of its window is throttled by the
                    // browser (about one frame a second), which makes every input to it take about a second.
                    var res = await _connection!.SendAsync("Target.createTarget", new { url = newUrl, newWindow = true }, null, ct).ConfigureAwait(false);
                    if (res.TryGetProperty("targetId", out var tidProp))
                    {
                        var newTargetId = tidProp.GetString()!;
                        _activeTabId = GetOrCreateTabId(newTargetId);
                    }
                    break;

                case "close_tab":
                    inputSent = true;
                    await _connection!.SendAsync("Target.closeTarget", new { targetId = session.TargetId }, null, ct).ConfigureAwait(false);
                    break;

                case "dialog_accept":
                case "dialog_dismiss":
                    inputSent = true;
                    await session.HandleDialogAsync(req.Action == "dialog_accept", ct).ConfigureAwait(false);
                    break;

                case "switch_tab":
                    inputSent = true;
                    await _connection!.SendAsync("Target.activateTarget", new { targetId = session.TargetId }, null, ct).ConfigureAwait(false);
                    _activeTabId = session.TabId;
                    break;

                default:
                    return ActResult.Failed(false, ReasonCodes.UnsupportedAction, $"Unsupported navigation action '{req.Action}'", Timing.Total(sw.Elapsed.TotalMilliseconds));
            }
        }
        catch (Exception ex)
        {
            phases["input"] = swInput.Elapsed.TotalMilliseconds;
            string reason;
            if (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                reason = ReasonCodes.EngineTimeout;
            }
            else if (inputSent)
            {
                reason = ReasonCodes.InputInterrupted;
            }
            else
            {
                reason = ReasonCodes.NavigationFailed;
            }
            return new ActResult(ActStatus.Error, inputSent, reason, ex.Message, null, Channels.Api, new Timing(sw.Elapsed.TotalMilliseconds, phases));
        }

        phases["input"] = swInput.Elapsed.TotalMilliseconds;

        var swVerify = Stopwatch.StartNew();
        ActStatus status = ActStatus.Unverified;
        string? reasonCode = null;
        string? verifyDetail = null;
        if (session.HasOpenDialog && req.Action is not ("dialog_accept" or "dialog_dismiss"))
        {
            verifyDetail = "A JavaScript dialog is open. Read it with browser_observe, then answer it with dialog_dismiss or dialog_accept.";
        }
        else if (req.Expect is not null)
        {
            // Input is already sent: a failure while waiting must come back as a result, never as an exception.
            try
            {
                var waitRes = await WaitForAsync(session.TabId, req.Expect.Condition, req.Expect.EffectiveTimeoutMs, ct).ConfigureAwait(false);
                status = waitRes.Met ? ActStatus.Confirmed : ActStatus.Timeout;
                if (!waitRes.Met)
                {
                    reasonCode = waitRes.Reason;
                }
            }
            catch (Exception ex)
            {
                phases["verify"] = swVerify.Elapsed.TotalMilliseconds;
                var timing = new Timing(sw.Elapsed.TotalMilliseconds, phases);
                return ct.IsCancellationRequested
                    ? new ActResult(ActStatus.Error, inputSent, ReasonCodes.InputInterrupted, "Cancelled after input was sent; the outcome is unknown", null, Channels.Api, timing)
                    : new ActResult(ActStatus.Unverified, inputSent, null, $"Input was sent but the page changed or could not be read during verify: {ex.Message}", null, Channels.Api, timing);
            }
        }
        phases["verify"] = swVerify.Elapsed.TotalMilliseconds;

        return new ActResult(status, inputSent, reasonCode, verifyDetail, null, Channels.Api, new Timing(sw.Elapsed.TotalMilliseconds, phases));
    }

    public async Task<Observation> ObserveAsync(BrowserObserveRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var swTotal = Stopwatch.StartNew();
        var phases = new Dictionary<string, double>();

        await EnsureConnectedAsync(null, ct).ConfigureAwait(false);
        var session = GetSessionForTab(req.Tab);
        if (session is null)
        {
            throw new InvalidOperationException("No active browser tab found for observation");
        }

        if (session.HasOpenDialog)
        {
            // A JavaScript dialog freezes the page: report the dialog (page-controlled text, quoted) and do not scan.
            var lastGen = _lastScans.TryGetValue(session.TabId, out var lastScan) ? lastScan.Generation : 0;
            var dialogText = new StringBuilder(ResultFormatter.UntrustedHeader).Append('\n')
                .Append("tab ").Append(session.TabId).Append(' ').Append(ResultFormatter.Quote(session.CurrentTitle, ResultFormatter.MaxNameChars))
                .Append(" url=").Append(ResultFormatter.Quote(session.CurrentUrl, ResultFormatter.MaxValueChars)).Append(" gen=").Append(lastGen).Append(" dialog_open").Append('\n')
                .Append("  dialog ").Append(ResultFormatter.Quote(session.OpenDialogMessage, ResultFormatter.MaxNameChars)).Append(" modal [dialog_accept,dialog_dismiss]")
                .ToString();
            return new Observation(session.TabId, lastGen, dialogText, 0, 0, false, null, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        var swRead = Stopwatch.StartNew();
        var scan = await ScanTabAsync(session, req.MaxElements, req.IncludeText, req.ViewportOnly, ct).ConfigureAwait(false);

        _lastScans[session.TabId] = scan;

        var elementDict = scan.Elements.ToDictionary(e => e.Ref, e => e);
        _cachedElements[session.TabId] = elementDict;
        var tabHistory = _history.GetOrAdd(session.TabId, _ => new());
        RecordHistory(tabHistory, scan.Generation, elementDict);

        phases["read"] = swRead.Elapsed.TotalMilliseconds;

        var swFormat = Stopwatch.StartNew();
        Observation obs;

        // Check if diff requested and valid
        if (req.SinceGeneration.HasValue && tabHistory.TryGetValue(req.SinceGeneration.Value, out var baseElements))
        {
            var diffText = BuildDiffText(session.TabId, req.SinceGeneration.Value, scan.Generation, baseElements, elementDict);
            phases["format"] = swFormat.Elapsed.TotalMilliseconds;

            obs = new Observation(
                session.TabId,
                scan.Generation,
                diffText,
                scan.Elements.Count,
                scan.OmittedOffscreen + scan.OmittedNonInteractive,
                true,
                req.SinceGeneration.Value,
                new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }
        else
        {
            var frames = 1;
            if (scan.Frames > 0)
            {
                try
                {
                    frames = Math.Max(await session.CountFramesAsync(ct).ConfigureAwait(false), 1 + scan.Frames);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The count is informational; the scan itself succeeded.
                }
            }

            var text = BuildObservationText(session.TabId, session.CurrentTitle, session.CurrentUrl, scan, req.MaxElements, frames);
            phases["format"] = swFormat.Elapsed.TotalMilliseconds;

            obs = new Observation(
                session.TabId,
                scan.Generation,
                text,
                scan.Elements.Count,
                scan.OmittedOffscreen + scan.OmittedNonInteractive,
                false,
                null,
                new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        _metrics.Record("browser_observe", swTotal.Elapsed.TotalMilliseconds);
        return obs;
    }

    public async Task<ActResult> ActAsync(BrowserActRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var risk = new RiskBox();

        // The act runs on the tab the ref belongs to (not the active tab), one act at a time per tab.
        await EnsureConnectedAsync(null, ct).ConfigureAwait(false);
        var actSession = GetSessionForRef(req.Ref) ?? GetActiveSession();
        TabLease? lease = null;
        if (actSession is not null)
        {
            lease = await TryEnterTabAsync(actSession, ct).ConfigureAwait(false);
            if (lease is null)
            {
                return ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0));
            }
        }

        ActResult result;
        using (lease)
        {
            result = await ActCoreAsync(req, ct, risk, actSession).ConfigureAwait(false);
        }

        // Q6: the Risky label only records. The action is classified once the live element has been read, so a result
        // returned before that (unknown generation, element gone, no isolated world) carries no label.
        return risk.Value is { } label ? result with { Risk = label } : result;
    }

    private sealed class RiskBox
    {
        public RiskClass? Value { get; set; }
    }

    private async Task<ActResult> ActCoreAsync(BrowserActRequest req, CancellationToken ct, RiskBox risk, CdpSession? session)
    {
        ArgumentNullException.ThrowIfNull(req);

        var swTotal = Stopwatch.StartNew();
        var phases = new Dictionary<string, double>();

        if (session is null)
        {
            return ActResult.Failed(false, ReasonCodes.BrowserNotConnected, "Browser is not connected", Timing.Total(swTotal.Elapsed.TotalMilliseconds));
        }

        // Phase 1: resolve
        var swResolve = Stopwatch.StartNew();
        IReadOnlyDictionary<string, ElementNodeData>? baseElements = null;
        ElementNodeData? observedElement = null;
        if (_history.TryGetValue(session.TabId, out var tabHist) && tabHist.TryGetValue(req.Generation, out var genElements))
        {
            baseElements = genElements;
            genElements.TryGetValue(req.Ref, out observedElement);
        }

        // N3: an act needs an observed baseline to compare the live element against.
        if (observedElement is null)
        {
            phases["resolve"] = swResolve.Elapsed.TotalMilliseconds;
            var why = baseElements is null
                ? $"Generation {req.Generation} is not a known observation for this tab; observe again"
                : $"Element '{req.Ref}' was not part of observation generation {req.Generation}; observe again";
            return new ActResult(ActStatus.Stale, false, ReasonCodes.NotFound, why, null, null, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        if (session.HasOpenDialog)
        {
            // A JavaScript dialog freezes the page, so the element cannot even be read. Nothing is sent.
            phases["resolve"] = swResolve.Elapsed.TotalMilliseconds;
            return ActResult.Blocked(ReasonCodes.ModalBlocking,
                $"Blocked by open dialog: {ResultFormatter.Quote(session.OpenDialogMessage, ResultFormatter.MaxNameChars)}. Use browser_nav dialog_dismiss or dialog_accept.",
                new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        // Re-read target live in isolated world (S2). N4: if the isolated world is unavailable, refuse; never use the page world.
        LiveElementInspection live;
        try
        {
            live = await session.InspectElementAsync(req.Ref, ct).ConfigureAwait(false);
        }
        catch (IsolatedWorldUnavailableException ex)
        {
            phases["resolve"] = swResolve.Elapsed.TotalMilliseconds;
            return ActResult.Blocked(ReasonCodes.BrowserNotConnected, ex.Message, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        if (!live.Found)
        {
            phases["resolve"] = swResolve.Elapsed.TotalMilliseconds;
            return new ActResult(ActStatus.Stale, false, ReasonCodes.NotFound, $"Element '{req.Ref}' is no longer connected to the DOM", null, null, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        // Classify for the Q6 label from the live element (label only; it changes nothing below).
        risk.Value = ClassifyActRisk(req.Action, live, req.Action == "press" ? (req.Value ?? "Enter") : req.Value);

        // Validate generation stability (S2 + S3)
        {
            if (!string.Equals(live.Role, observedElement.Role, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(live.Name, observedElement.Name, StringComparison.Ordinal))
            {
                phases["resolve"] = swResolve.Elapsed.TotalMilliseconds;
                return new ActResult(ActStatus.Stale, false, ReasonCodes.NotFound, $"Element '{req.Ref}' changed across generations (role: '{observedElement.Role}'->'{live.Role}', name: '{observedElement.Name}'->'{live.Name}')", null, null, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
            }

            if (observedElement.Bounds is not null && live.Bounds is not null)
            {
                var dx = Math.Abs(live.Bounds.X - observedElement.Bounds.X);
                var dy = Math.Abs(live.Bounds.Y - observedElement.Bounds.Y);
                var dw = Math.Abs(live.Bounds.Width - observedElement.Bounds.Width);
                var dh = Math.Abs(live.Bounds.Height - observedElement.Bounds.Height);
                if (dx > 4 || dy > 4 || dw > 4 || dh > 4)
                {
                    phases["resolve"] = swResolve.Elapsed.TotalMilliseconds;
                    return new ActResult(ActStatus.Stale, false, ReasonCodes.NotFound, $"Element '{req.Ref}' moved or resized across generations (dx={dx:F1}, dy={dy:F1}, dw={dw:F1}, dh={dh:F1})", null, null, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
                }
            }
        }
        phases["resolve"] = swResolve.Elapsed.TotalMilliseconds;

        // Phase 2: precheck
        var swPrecheck = Stopwatch.StartNew();
        if (!live.IsEnabled)
        {
            phases["precheck"] = swPrecheck.Elapsed.TotalMilliseconds;
            return ActResult.Blocked(ReasonCodes.Disabled, "Element is disabled", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        if (session.HasOpenDialog)
        {
            phases["precheck"] = swPrecheck.Elapsed.TotalMilliseconds;
            return ActResult.Blocked(ReasonCodes.ModalBlocking, $"Blocked by open dialog: {ResultFormatter.Quote(session.OpenDialogMessage, ResultFormatter.MaxNameChars)}. Use browser_nav dialog_dismiss or dialog_accept.", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        if (req.Action is "click" or "double_click" or "hover")
        {
            if (live.Occlusion is not null || !live.IsExactHit)
            {
                phases["precheck"] = swPrecheck.Elapsed.TotalMilliseconds;
                return ActResult.Blocked(live.Occlusion ?? ReasonCodes.OccludedSameWindow, "Element is occluded or moved", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
            }
        }
        phases["precheck"] = swPrecheck.Elapsed.TotalMilliseconds;

        // Phase 3: gate
        var swGate = Stopwatch.StartNew();
        var procName = BrowserProcessName;
        var descriptor = new ElementDescriptor(
            req.Ref,
            "browser",
            procName,
            session.CurrentTitle,
            session.CurrentUrl,
            live.Role ?? "",
            live.Name ?? "",
            live.AutomationId,
            live.IsPassword,
            live.IsEnabled,
            live.Path ?? []);

        var actionDesc = new ActionDescriptor(
            "browser_act",
            "browser",
            req.Action,
            descriptor,
            procName,
            session.CurrentTitle,
            session.CurrentUrl,
            live.IsPassword ? "<redacted>" : req.Value,
            req.Action == "press" ? req.Value : null);

        var gateResult = await _safetyGate.AuthorizeAsync(actionDesc, ct).ConfigureAwait(false);
        phases["gate"] = swGate.Elapsed.TotalMilliseconds;

        if (!gateResult.Allowed)
        {
            return ActResult.Denied(
                gateResult.Reason ?? ReasonCodes.PolicyForbidden,
                gateResult.Detail ?? "Action denied by safety policy",
                new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        if (gateResult.UserConfirmed)
        {
            // Re-read target live after confirmation delay (Q4)
            try
            {
                live = await session.InspectElementAsync(req.Ref, ct).ConfigureAwait(false);
            }
            catch (IsolatedWorldUnavailableException ex)
            {
                return ActResult.Blocked(ReasonCodes.BrowserNotConnected, ex.Message, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
            }

            if (!live.Found ||
                !string.Equals(live.Role, observedElement.Role, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(live.Name, observedElement.Name, StringComparison.Ordinal))
            {
                return new ActResult(ActStatus.Stale, false, ReasonCodes.NotFound, "Target element changed while confirmation dialog was open", null, null, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
            }
        }

        // Check kill switch and user activity before input dispatch
        if (_killSwitch.IsTripped)
        {
            return ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        if (_userActivity.UserActiveRecently)
        {
            return ActResult.Blocked(ReasonCodes.UserActive, "User active recently", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        // Phase 4: input
        var swInput = Stopwatch.StartNew();
        var channel = MapChannel(req.Action);
        var inputSent = false;
        string? expectedTypeValue = null;

        ActResult InputFailure(string reason, string detail)
        {
            phases["input"] = swInput.Elapsed.TotalMilliseconds;
            return new ActResult(ActStatus.Error, inputSent, reason, detail, null, channel, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        try
        {
            switch (req.Action.ToLowerInvariant())
            {
                case "click":
                    if (!live.IsExactHit || live.Center is null)
                    {
                        phases["input"] = swInput.Elapsed.TotalMilliseconds;
                        return ActResult.Blocked(ReasonCodes.OccludedSameWindow, "Target element moved or was covered before click dispatch", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
                    }
                    await session.ClickAsync(live.Center.X, live.Center.Y, () => inputSent = true, ct).ConfigureAwait(false);
                    inputSent = true;
                    break;

                case "double_click":
                    if (!live.IsExactHit || live.Center is null)
                    {
                        phases["input"] = swInput.Elapsed.TotalMilliseconds;
                        return ActResult.Blocked(ReasonCodes.OccludedSameWindow, "Target element moved or was covered before double click dispatch", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
                    }
                    await session.DoubleClickAsync(live.Center.X, live.Center.Y, () => inputSent = true, ct).ConfigureAwait(false);
                    inputSent = true;
                    break;

                case "hover":
                    if (!live.IsExactHit || live.Center is null)
                    {
                        phases["input"] = swInput.Elapsed.TotalMilliseconds;
                        return ActResult.Blocked(ReasonCodes.OccludedSameWindow, "Target element moved or was covered before hover dispatch", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
                    }
                    await session.HoverAsync(live.Center.X, live.Center.Y, () => inputSent = true, ct).ConfigureAwait(false);
                    inputSent = true;
                    break;

                case "type":
                {
                    // N1: focus the target ref first, then confirm it really holds focus before any text is sent.
                    var typeFocus = await session.ExecuteInPageActionAsync(req.Ref, "focus", null, () => inputSent = true, ct).ConfigureAwait(false);
                    inputSent = true;
                    if (!typeFocus.Success)
                    {
                        return InputFailure(MapPageFailure(typeFocus.Reason), $"Could not focus '{req.Ref}' before typing; no text was sent");
                    }

                    var textToType = req.Value ?? string.Empty;
                    // Check kill switch before typing chunks
                    if (_killSwitch.IsTripped)
                    {
                        phases["input"] = swInput.Elapsed.TotalMilliseconds;
                        return new ActResult(ActStatus.Error, true, ReasonCodes.InputInterrupted, "OmniWin stop key tripped during type input", null, channel, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
                    }

                    expectedTypeValue = ComputeExpectedTypeValue(live.Value, live.SelectionStart, live.SelectionEnd, textToType);

                    // Last check, right before the text goes out: the focused element must still be exactly the target.
                    var typeStillFocused = await session.IsFocusedAsync(req.Ref, ct).ConfigureAwait(false);
                    if (!typeStillFocused.Success)
                    {
                        return InputFailure(MapPageFailure(typeStillFocused.Reason), $"Focus moved away from '{req.Ref}' before typing; no text was sent");
                    }

                    await session.InsertTextAsync(textToType, () => inputSent = true, ct).ConfigureAwait(false);
                    inputSent = true;
                    break;
                }

                case "press":
                {
                    // N1: a key goes to whatever has focus, so focus the target ref first and confirm it.
                    var pressFocus = await session.ExecuteInPageActionAsync(req.Ref, "focus", null, () => inputSent = true, ct).ConfigureAwait(false);
                    inputSent = true;
                    if (!pressFocus.Success)
                    {
                        return InputFailure(MapPageFailure(pressFocus.Reason), $"Could not focus '{req.Ref}' before the key press; no key was sent");
                    }

                    if (_killSwitch.IsTripped)
                    {
                        phases["input"] = swInput.Elapsed.TotalMilliseconds;
                        return new ActResult(ActStatus.Error, true, ReasonCodes.InputInterrupted, "OmniWin stop key tripped during key press", null, channel, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
                    }

                    var pressStillFocused = await session.IsFocusedAsync(req.Ref, ct).ConfigureAwait(false);
                    if (!pressStillFocused.Success)
                    {
                        return InputFailure(MapPageFailure(pressStillFocused.Reason), $"Focus moved away from '{req.Ref}' before the key press; no key was sent");
                    }

                    await session.PressKeyAsync(req.Value ?? "Enter", () => inputSent = true, ct).ConfigureAwait(false);
                    inputSent = true;
                    break;
                }

                case "set_value":
                case "clear":
                case "check":
                case "uncheck":
                case "select":
                case "focus":
                case "scroll_into_view":
                {
                    var pageAction = req.Action.ToLowerInvariant();
                    var pageValue = pageAction is "set_value" or "select" ? req.Value : null;
                    var pageRes = await session.ExecuteInPageActionAsync(req.Ref, pageAction, pageValue, () => inputSent = true, ct).ConfigureAwait(false);
                    inputSent = true;
                    if (!pageRes.Success)
                    {
                        return InputFailure(MapPageFailure(pageRes.Reason), $"Page action '{pageAction}' on '{req.Ref}' failed ({pageRes.Reason ?? "unknown"})");
                    }
                    break;
                }

                default:
                    phases["input"] = swInput.Elapsed.TotalMilliseconds;
                    return ActResult.Failed(false, ReasonCodes.UnsupportedAction, $"Action '{req.Action}' not supported", new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
            }
        }
        catch (Exception ex)
        {
            phases["input"] = swInput.Elapsed.TotalMilliseconds;
            string errReason;
            if (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                errReason = ReasonCodes.EngineTimeout;
            }
            else if (inputSent)
            {
                errReason = ReasonCodes.InputInterrupted;
            }
            else
            {
                errReason = ReasonCodes.NavigationFailed;
            }
            return new ActResult(ActStatus.Error, inputSent, errReason, ex.Message, null, channel, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }
        phases["input"] = swInput.Elapsed.TotalMilliseconds;

        // Phase 5: verify. The input is already sent, so a failure here (for example the page navigated and its
        // isolated world was destroyed mid-scan) must come back as a result with input_sent, never as an exception.
        var swVerify = Stopwatch.StartNew();
        if (session.HasOpenDialog)
        {
            // The input opened a JavaScript dialog, which freezes the page: nothing can be scanned until it is answered.
            phases["verify"] = swVerify.Elapsed.TotalMilliseconds;
            return new ActResult(ActStatus.Unverified, inputSent, null,
                "Input was sent and a JavaScript dialog opened. Read it with browser_observe, then answer it with browser_nav dialog_dismiss or dialog_accept.",
                null, channel, new Timing(swTotal.Elapsed.TotalMilliseconds, phases));
        }

        try
        {
            var afterScan = await ScanTabAsync(session, 150, false, false, ct).ConfigureAwait(false);
            var afterElements = afterScan.Elements.ToDictionary(e => e.Ref, e => e);
            _cachedElements[session.TabId] = afterElements;
            var actTabHist = _history.GetOrAdd(session.TabId, _ => new());
            RecordHistory(actTabHist, afterScan.Generation, afterElements);

            var afterObs = new Observation(
                session.TabId,
                afterScan.Generation,
                BuildDiffText(session.TabId, req.Generation, afterScan.Generation, baseElements ?? new Dictionary<string, ElementNodeData>(), afterElements),
                afterScan.Elements.Count,
                afterScan.OmittedOffscreen + afterScan.OmittedNonInteractive,
                true,
                req.Generation,
                Timing.Total(0));

            ActStatus status = ActStatus.Confirmed;
            string? reason = null;
            string? detail = null;

            if (req.Expect is not null)
            {
                var wait = await WaitForAsync(session.TabId, req.Expect.Condition, req.Expect.EffectiveTimeoutMs, ct).ConfigureAwait(false);
                if (!wait.Met)
                {
                    status = ActStatus.Timeout;
                    reason = wait.Reason;
                }
            }
            else
            {
                // Default expectation check from §10.4
                switch (req.Action.ToLowerInvariant())
                {
                    case "type":
                        if (afterElements.TryGetValue(req.Ref, out var typedEl))
                        {
                            var expected = expectedTypeValue ?? req.Value;
                            if (!live.IsPassword && expected is not null && !string.Equals(typedEl.Value, expected, StringComparison.Ordinal))
                            {
                                status = ActStatus.Contradicted;
                                reason = ReasonCodes.ReadbackMismatch;
                                detail = $"Readback '{typedEl.Value}' did not match expected '{expected}'";
                            }
                        }
                        break;

                    case "set_value":
                        if (afterElements.TryGetValue(req.Ref, out var updatedEl))
                        {
                            if (!live.IsPassword && req.Value is not null && !string.Equals(updatedEl.Value, req.Value, StringComparison.Ordinal))
                            {
                                status = ActStatus.Contradicted;
                                reason = ReasonCodes.ReadbackMismatch;
                                detail = $"Readback '{updatedEl.Value}' did not match expected '{req.Value}'";
                            }
                        }
                        break;

                    case "clear":
                        if (afterElements.TryGetValue(req.Ref, out var clearedEl) && !string.IsNullOrEmpty(clearedEl.Value))
                        {
                            status = ActStatus.Contradicted;
                            reason = ReasonCodes.ReadbackMismatch;
                        }
                        break;

                    case "check":
                        if (afterElements.TryGetValue(req.Ref, out var checkedEl) && !checkedEl.States.Contains("checked"))
                        {
                            status = ActStatus.Contradicted;
                        }
                        break;

                    case "uncheck":
                        if (afterElements.TryGetValue(req.Ref, out var uncheckedEl) && uncheckedEl.States.Contains("checked"))
                        {
                            status = ActStatus.Contradicted;
                        }
                        break;

                    case "click":
                    case "double_click":
                    case "hover":
                    case "press":
                        // Check if anything visible changed
                        if (afterScan.Generation == req.Generation)
                        {
                            status = ActStatus.Unverified;
                            detail = "no visible change";
                        }
                        else
                        {
                            status = ActStatus.Unverified;
                        }
                        break;
                }
            }

            phases["verify"] = swVerify.Elapsed.TotalMilliseconds;
            var totalMs = swTotal.Elapsed.TotalMilliseconds;
            _metrics.Record("browser_act", totalMs);

            return new ActResult(status, inputSent, reason, detail, afterObs, channel, new Timing(totalMs, phases));
        }
        catch (Exception ex)
        {
            phases["verify"] = swVerify.Elapsed.TotalMilliseconds;
            var verifyTiming = new Timing(swTotal.Elapsed.TotalMilliseconds, phases);
            if (ct.IsCancellationRequested)
            {
                return new ActResult(ActStatus.Error, inputSent, ReasonCodes.InputInterrupted, "Cancelled after input was sent; the outcome is unknown", null, channel, verifyTiming);
            }

            // The input navigated the page (an Enter that starts a search), so the old page could not be scanned. When the caller said
            // where the browser should land, check that on the new page instead of giving up (reviewer, 2026-11-18).
            if (inputSent && req.Expect is { Condition: UrlContains } landing)
            {
                try
                {
                    var landed = await WaitForAsync(session.TabId, landing.Condition, landing.EffectiveTimeoutMs, ct).ConfigureAwait(false);
                    phases["verify"] = swVerify.Elapsed.TotalMilliseconds;
                    var landedTiming = new Timing(swTotal.Elapsed.TotalMilliseconds, phases);
                    return landed.Met
                        ? new ActResult(ActStatus.Confirmed, true, null, "The page navigated and arrived where expected.", null, channel, landedTiming)
                        : new ActResult(ActStatus.Timeout, true, landed.Reason, "The page navigated but did not arrive where expected.", null, channel, landedTiming);
                }
                catch (Exception again) when (again is not OperationCanceledException)
                {
                    // fall through to the plain answer below
                }
            }

            return new ActResult(ActStatus.Unverified, inputSent, null, $"Input was sent but the page changed or could not be read during verify: {ex.Message}", null, channel, verifyTiming);
        }
    }

    public async Task<WaitResult> WaitForAsync(string? tab, Expectation condition, int timeoutMs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var sw = Stopwatch.StartNew();
        var clampedTimeout = Math.Clamp(timeoutMs, 1, ExpectSpec.MaxTimeoutMs);
        var needTabs = NeedsTabs(condition);
        var tabOnly = IsTabOnly(condition);
        // A tab condition (window_closed after close_tab) must still work when the named tab's session is gone.
        var session = GetSessionForTab(tab) ?? (needTabs ? GetActiveSession() : null);
        if (session is null)
        {
            return new WaitResult(false, ReasonCodes.BrowserNotConnected, null, Timing.Total(sw.Elapsed.TotalMilliseconds));
        }

        while (sw.ElapsedMilliseconds < clampedTimeout)
        {
            ct.ThrowIfCancellationRequested();

            if (session.HasOpenDialog)
            {
                // A JavaScript dialog freezes the page, so no condition about it can be read until the dialog is answered.
                return new WaitResult(false, ReasonCodes.ModalBlocking, null, Timing.Total(sw.Elapsed.TotalMilliseconds));
            }

            ElementScanResult scan;
            try
            {
                if (tabOnly)
                {
                    // window_opened / window_closed are decided from the tab list alone. Never scan a page for them: the tab
                    // the condition is about may be closing, and a command to a closed tab can go unanswered.
                    scan = new ElementScanResult(0, 1, [], 0, 0, session.CurrentUrl, session.CurrentTitle);
                }
                else
                {
                    scan = await ScanTabAsync(session, 150, false, false, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (needTabs && ex is not OperationCanceledException)
            {
                // A tab condition (window_closed) can be decided without reading a page: the tab it is about may be gone.
                scan = new ElementScanResult(0, 1, [], 0, 0, session.CurrentUrl, session.CurrentTitle);
            }

            var tabs = needTabs ? await ListTabsAsync(ct).ConfigureAwait(false) : null;

            if (EvaluateCondition(condition, session, scan, tabs))
            {
                return new WaitResult(true, null, null, Timing.Total(sw.Elapsed.TotalMilliseconds));
            }

            // Bounded polling fallback (25ms) per contract §11
            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        return new WaitResult(false, ReasonCodes.EngineTimeout, null, Timing.Total(sw.Elapsed.TotalMilliseconds));
    }

    /// <summary>
    /// <c>browser_view</c>: a look-only picture of a tab. Password and payment fields (and cross-origin frames) are
    /// blacked out on the full-resolution pixels before the image is scaled and encoded; if that can't be done there is
    /// no image. It sends no input, so the user-activity pause doesn't apply; the stop key does.
    /// </summary>
    public async Task<ViewOutcome> ViewTabAsync(string? tab, bool fullPage, int? maxSide, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var phases = new Dictionary<string, double>();
        Timing Now() => new(timer.Elapsed.TotalMilliseconds, new Dictionary<string, double>(phases));
        ViewOutcome Fail(ActResult result) => new(null, result with { Timing = Now() }, string.Empty, string.Empty, DateTimeOffset.UtcNow, Now());

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _killSwitch.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(20));
        var token = linked.Token;

        try
        {
            await EnsureConnectedAsync(null, token).ConfigureAwait(false);
            var session = GetSessionForTab(tab);
            if (session is null)
            {
                return Fail(ActResult.Failed(false, ReasonCodes.BrowserNotConnected, "Browser tab not found", Timing.Total(0)));
            }

            if (_killSwitch.IsTripped)
            {
                return Fail(ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0)));
            }

            if (session.HasOpenDialog)
            {
                return Fail(ActResult.Blocked(ReasonCodes.ModalBlocking, "A JavaScript dialog is open; the page can't be captured until it is answered.", Timing.Total(0)));
            }

            var swGate = Stopwatch.StartNew();
            var gateResult = await _safetyGate.AuthorizeAsync(new ActionDescriptor(
                "browser_view", "browser", "read", null, BrowserProcessName, session.CurrentTitle, session.CurrentUrl, null, null), token).ConfigureAwait(false);
            phases["gate"] = swGate.Elapsed.TotalMilliseconds;
            if (!gateResult.Allowed)
            {
                return Fail(ActResult.Denied(gateResult.Reason ?? ReasonCodes.PolicyForbidden, gateResult.Detail ?? "Denied by safety policy", Timing.Total(0)));
            }

            var swCapture = Stopwatch.StartNew();
            var capturedAt = DateTimeOffset.UtcNow;
            CdpSession.ViewCapture capture;
            try
            {
                capture = await session.CaptureViewAsync(fullPage, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Without the sensitive areas there is nothing safe to return.
                return Fail(ActResult.Failed(false, ReasonCodes.NotFound, "The tab could not be captured and masked, so no image is returned.", Timing.Total(0)));
            }

            phases["capture"] = swCapture.Elapsed.TotalMilliseconds;

            var swRender = Stopwatch.StartNew();
            ViewImage image;
            try
            {
                image = await ViewImaging.RenderAsync(capture.Png, capture.Masks, null, ViewLimits.Clamp(maxSide), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(ActResult.Failed(false, ReasonCodes.NotFound, "The image could not be masked and encoded, so no image is returned.", Timing.Total(0)));
            }

            phases["render"] = swRender.Elapsed.TotalMilliseconds;

            if (_killSwitch.IsTripped)
            {
                return Fail(ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0)));
            }

            var timing = Now();
            _metrics.Record("browser_view", Math.Max(0, timing.TotalMs - phases.GetValueOrDefault("gate")));
            return new ViewOutcome(image, null, session.TabId, capture.Title, capturedAt, timing);
        }
        catch (OperationCanceledException)
        {
            return Fail(_killSwitch.IsTripped
                ? ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0))
                : ActResult.Failed(false, ReasonCodes.EngineTimeout, "The browser did not answer in time.", Timing.Total(0)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(ActResult.Failed(false, ReasonCodes.BrowserNotConnected, "The browser could not complete the view.", Timing.Total(0)));
        }
    }

    public async Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct)
    {
        var session = GetSessionForRef(elementRef) ?? GetActiveSession();
        if (session is null) return null;

        var live = await session.InspectElementAsync(elementRef, ct).ConfigureAwait(false);
        if (!live.Found) return null;

        var procName = BrowserProcessName;
        return new ElementDescriptor(
            elementRef,
            "browser",
            procName,
            session.CurrentTitle,
            session.CurrentUrl,
            live.Role ?? "",
            live.Name ?? "",
            live.AutomationId,
            live.IsPassword,
            live.IsEnabled,
            live.Path ?? []);
    }

    public async Task<ResolvedRef?> ResolveSelectorAsync(string urlPattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);

        var session = GetActiveSession();
        if (session is null) return null;

        if (!string.IsNullOrEmpty(urlPattern) && !session.CurrentUrl.Contains(urlPattern, StringComparison.OrdinalIgnoreCase))
        {
            // The active tab is not the one the selector is about: use the tab whose URL matches, if there is one.
            session = _sessions.Values.FirstOrDefault(s => s.CurrentUrl.Contains(urlPattern, StringComparison.OrdinalIgnoreCase));
            if (session is null) return null;
        }

        var scan = await ScanTabAsync(session, 150, false, false, ct).ConfigureAwait(false);

        // Record the scan like an observation, so an act on the resolved ref has an observed baseline (N3).
        var elementDict = scan.Elements.ToDictionary(e => e.Ref, e => e);
        _cachedElements[session.TabId] = elementDict;
        RecordHistory(_history.GetOrAdd(session.TabId, _ => new()), scan.Generation, elementDict);

        foreach (var el in scan.Elements)
        {
            if (MatchesPath(el.Path, path))
            {
                return new ResolvedRef(el.Ref, scan.Generation);
            }
        }

        return null;
    }

    private static bool MatchesPath(IReadOnlyList<SelectorSegment> actual, IReadOnlyList<SelectorSegment> expected)
    {
        if (expected.Count == 0) return false;
        if (actual.Count < expected.Count) return false;

        var lastActual = actual[^1];
        var lastExpected = expected[^1];

        if (!string.Equals(lastActual.Role, lastExpected.Role, StringComparison.OrdinalIgnoreCase))
            return false;

        if (lastExpected.AutomationId is not null && !string.Equals(lastActual.AutomationId, lastExpected.AutomationId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (lastExpected.Name is not null && !string.Equals(lastActual.Name, lastExpected.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsTabOnly(Expectation cond) => cond switch
    {
        WindowOpened or WindowClosed => true,
        AllOf all => all.Items.Count > 0 && all.Items.All(IsTabOnly),
        AnyOf any => any.Items.Count > 0 && any.Items.All(IsTabOnly),
        _ => false
    };

    private static bool NeedsTabs(Expectation cond) => cond switch
    {
        WindowOpened or WindowClosed => true,
        AllOf all => all.Items.Any(NeedsTabs),
        AnyOf any => any.Items.Any(NeedsTabs),
        _ => false
    };

    private static bool EvaluateCondition(Expectation cond, CdpSession session, ElementScanResult scan, IReadOnlyList<TabInfo>? tabs)
    {
        return cond switch
        {
            // Q7: in the browser a window is a tab. window_opened matches a tab by title; window_closed names a tN id.
            WindowOpened wo => tabs is not null &&
                tabs.Any(t => wo.TitleContains is null || t.Title.Contains(wo.TitleContains, StringComparison.OrdinalIgnoreCase)),
            WindowClosed wc => tabs is not null && wc.Window.StartsWith('t') && tabs.All(t => t.Id != wc.Window),
            UrlContains uc => session.CurrentUrl.Contains(uc.Value, StringComparison.OrdinalIgnoreCase),
            TextAppears ta => scan.Elements.Any(e => e.Name.Contains(ta.Value, StringComparison.OrdinalIgnoreCase) || e.Value.Contains(ta.Value, StringComparison.OrdinalIgnoreCase)),
            ElementAppears ea => scan.Elements.Any(e => (ea.Role is null || e.Role.Equals(ea.Role, StringComparison.OrdinalIgnoreCase)) &&
                                                        (ea.NameContains is null || e.Name.Contains(ea.NameContains, StringComparison.OrdinalIgnoreCase))),
            ElementGone eg => scan.Elements.All(e => e.Ref != eg.Ref),
            ValueEquals ve => scan.Elements.Any(e => e.Ref == ve.Ref && e.Value == ve.Value),
            FocusOn fo => scan.Elements.Any(e => e.Ref == fo.Ref && e.States.Contains("focused")),
            ToggleIs ti => scan.Elements.Any(e => e.Ref == ti.Ref && (ti.State == "on" ? e.States.Contains("checked") : !e.States.Contains("checked"))),
            AllOf ao => ao.Items.All(i => EvaluateCondition(i, session, scan, tabs)),
            AnyOf any => any.Items.Any(i => EvaluateCondition(i, session, scan, tabs)),
            _ => false
        };
    }

    private static string MapChannel(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "click" or "double_click" or "hover" => Channels.CdpMouse,
            "type" => Channels.CdpInsertText,
            "set_value" or "clear" => Channels.SetValue,
            "press" => Channels.Keys,
            "check" or "uncheck" => Channels.Toggle,
            "select" => Channels.Select,
            "focus" or "scroll_into_view" => Channels.Api,
            _ => Channels.Api
        };
    }

    private static string BuildObservationText(string tabId, string title, string url, ElementScanResult scan, int maxElements, int frames = 1)
    {
        var sb = new StringBuilder(1024);
        sb.Append(ResultFormatter.UntrustedHeader).Append('\n');
        sb.Append("tab ").Append(tabId).Append(' ').Append(ResultFormatter.Quote(title, ResultFormatter.MaxNameChars))
          .Append(" url=").Append(ResultFormatter.Quote(url, ResultFormatter.MaxValueChars)).Append(" gen=").Append(scan.Generation).Append(" frames=").Append(frames).Append('\n');

        var shown = 0;
        foreach (var el in scan.Elements)
        {
            if (shown >= maxElements) break;

            sb.Append("  ").Append(el.Ref).Append(' ');
            if (el.Frame is not null)
            {
                sb.Append('(').Append(el.Frame).Append(") ");
            }

            sb.Append(el.Role).Append(' ')
              .Append(ResultFormatter.Quote(el.Name, ResultFormatter.MaxNameChars));

            if (el.IsPassword)
            {
                sb.Append(" value=<redacted>");
            }
            else if (!string.IsNullOrEmpty(el.Value))
            {
                sb.Append(" value=").Append(ResultFormatter.Quote(el.Value, ResultFormatter.MaxValueChars));
            }

            foreach (var state in el.States)
            {
                sb.Append(' ').Append(state);
            }

            if (el.Actions.Count > 0)
            {
                sb.Append(" [").Append(string.Join(',', el.Actions)).Append(']');
            }

            sb.Append('\n');
            shown++;
        }

        foreach (var frame in scan.CrossOriginFrames ?? [])
        {
            // Cross-origin frames are counted in frames=N but their contents cannot be indexed (Q9).
            sb.Append("  frame ").Append(frame.Name).Append(" cross_origin origin=").Append(ResultFormatter.Quote(frame.Origin, ResultFormatter.MaxValueChars)).Append('\n');
        }

        var omitted = scan.OmittedOffscreen + scan.OmittedNonInteractive;
        sb.Append("omitted: ").Append(scan.OmittedOffscreen).Append(" offscreen, ")
          .Append(scan.OmittedNonInteractive).Append(" non-interactive");
        if (scan.CappedAtLimit > 0)
        {
            sb.Append(", ").Append(scan.CappedAtLimit).Append(" beyond the scan limit");
        }

        if (scan.Elements.Count > maxElements)
        {
            sb.Append('\n');
            sb.Append("truncated: ").Append(scan.Elements.Count - maxElements).Append(" more (raise max_elements or narrow scope)");
        }

        if (scan.CappedAtLimit > 0)
        {
            // The page has more candidates than one read inspects. Say so; the elements listed are the first ones in document order.
            sb.Append('\n');
            sb.Append("truncated: ").Append(scan.CappedAtLimit).Append(" more not read (page scan limit; use viewport_only or narrow scope)");
        }

        if (scan.FramesNotRead > 0)
        {
            // A same-origin frame that is still loading (or changed while it was read) adds nothing to this list. Say so, so
            // the model observes again instead of concluding the element is not there.
            sb.Append('\n');
            sb.Append("frames_not_read: ").Append(scan.FramesNotRead).Append(" (still loading or changed while read; observe again)");
        }

        return sb.ToString();
    }

    private static string BuildDiffText(
        string tabId,
        long baseGen,
        long currentGen,
        IReadOnlyDictionary<string, ElementNodeData> prev,
        IReadOnlyDictionary<string, ElementNodeData> curr)
    {
        var sb = new StringBuilder(512);
        sb.Append(ResultFormatter.UntrustedHeader).Append('\n');
        sb.Append("diff ").Append(tabId).Append(" gen ").Append(baseGen).Append("->").Append(currentGen).Append('\n');

        foreach (var (refId, el) in curr)
        {
            if (!prev.TryGetValue(refId, out var oldEl))
            {
                sb.Append("+ ").Append(refId).Append(' ').Append(el.Role).Append(' ')
                  .Append(ResultFormatter.Quote(el.Name, ResultFormatter.MaxNameChars));
                if (el.Actions.Count > 0)
                {
                    sb.Append(" [").Append(string.Join(',', el.Actions)).Append(']');
                }
                sb.Append('\n');
            }
            else
            {
                if (el.Value != oldEl.Value)
                {
                    sb.Append("~ ").Append(refId).Append(" value: ")
                      .Append(oldEl.IsPassword ? "<redacted>" : ResultFormatter.Quote(oldEl.Value, 40)).Append(" -> ")
                      .Append(el.IsPassword ? "<redacted>" : ResultFormatter.Quote(el.Value, 40)).Append('\n');
                }

                var addedStates = el.States.Except(oldEl.States).ToList();
                var removedStates = oldEl.States.Except(el.States).ToList();
                if (addedStates.Count > 0 || removedStates.Count > 0)
                {
                    sb.Append("~ ").Append(refId).Append(" states: ");
                    foreach (var s in addedStates) sb.Append('+').Append(s).Append(' ');
                    foreach (var s in removedStates) sb.Append('-').Append(s).Append(' ');
                    sb.Append('\n');
                }
            }
        }

        foreach (var (refId, _) in prev)
        {
            if (!curr.ContainsKey(refId))
            {
                sb.Append("- ").Append(refId).Append('\n');
            }
        }

        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var s in _sessions.Values)
        {
            try { await s.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
        _sessions.Clear();

        if (_connection is not null)
        {
            try { await _connection.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _connection = null;
        }

        if (_launcher is not null)
        {
            try { await _launcher.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _launcher = null;
        }

        _lock.Dispose();
    }
}
