using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.Browser;

public sealed record ElementScanResult(
    long Generation,
    int NextRefId,
    IReadOnlyList<ElementNodeData> Elements,
    int OmittedOffscreen,
    int OmittedNonInteractive,
    string Url,
    string Title,
    int Frames = 0,                                          // iframe/frame elements in the page (0 = no frame work needed)
    IReadOnlyList<CrossOriginFrame>? CrossOriginFrames = null,
    IReadOnlyList<FrameDetail>? FrameDetails = null,
    int CappedAtLimit = 0,                                   // candidates past the per-scan cap, counted but not read
    int FramesNotRead = 0);                                  // same-origin frames still loading or that failed to read this time

/// <summary>
/// One iframe element as the page script sees it: its src, whether its document can be read (same origin), and whether
/// that document is still loading.
/// </summary>
public sealed record FrameDetail(string Src, bool Accessible, bool Loading = false);

/// <summary>A child frame whose origin differs from the page: it is counted and listed but its contents are not indexed (Q9).</summary>
public sealed record CrossOriginFrame(string Name, string Origin);

public sealed record ElementBounds(double X, double Y, double Width, double Height);
public sealed record ElementCenter(double X, double Y);

public sealed record LiveElementInspection(
    bool Found,
    string? Ref = null,
    string? Role = null,
    string? Name = null,
    string? Value = null,
    bool IsPassword = false,
    bool IsEnabled = false,
    string? AutomationId = null,
    ElementBounds? Bounds = null,
    ElementCenter? Center = null,
    bool IsExactHit = false,
    string? Occlusion = null,
    bool IsOffscreen = false,
    IReadOnlyList<SelectorSegment>? Path = null,
    int? SelectionStart = null,
    int? SelectionEnd = null);

public sealed record ElementNodeData(
    string Ref,
    string Role,
    string Name,
    string Value,
    bool IsPassword,
    bool IsEnabled,
    string? AutomationId,
    IReadOnlyList<string> States,
    IReadOnlyList<string> Actions,
    ElementBounds Bounds,
    ElementCenter Center,
    string? Occlusion,
    bool IsOffscreen,
    IReadOnlyList<SelectorSegment> Path,
    string? Frame = null);            // "f2" for an element inside a same-origin child frame (Q9); null for the page itself

/// <summary>Outcome of an in-page action run in the isolated world.</summary>
public sealed record PageActionResult(bool Success, string? Reason);

/// <summary>The isolated world for the page could not be found or created. Callers must refuse to act, never fall back to the page world.</summary>
public sealed class IsolatedWorldUnavailableException : InvalidOperationException
{
    public IsolatedWorldUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class CdpSession : IAsyncDisposable
{
    private readonly CdpConnection _connection;
    private readonly TaskCompletionSource<bool> _loadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _navLock = new();
    private TaskCompletionSource<bool> _navigationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _frameLock = new();
    private readonly Dictionary<string, int> _frameContexts = new();   // frameId -> isolated world
    private readonly Dictionary<string, string> _refFrames = new();    // ref -> frameId, for refs found inside child frames
    private readonly Dictionary<string, string> _frameNames = new();   // frameId -> "f2"
    private int _nextFrameNumber = 2;
    private string? _currentDialogMessage;
    private TaskCompletionSource _dialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _isolatedContextId;
    private string? _rootFrameId;

    public CdpSession(string sessionId, string targetId, string tabId, CdpConnection connection)
    {
        SessionId = sessionId;
        TargetId = targetId;
        TabId = tabId;
        _connection = connection;
    }

    public string SessionId { get; }
    public string TargetId { get; }
    public string TabId { get; }
    public string CurrentUrl { get; private set; } = string.Empty;
    public string CurrentTitle { get; private set; } = string.Empty;

    /// <summary>
    /// Runs one input-bearing operation (act or navigation) at a time on this tab. Operations on different tabs never wait
    /// on each other.
    /// </summary>
    public SemaphoreSlim OpGate { get; } = new(1, 1);

    /// <summary>Distance between consecutive ref numbers this tab hands out (the engine's lane count), so tabs never reuse a ref.</summary>
    public int RefStride { get; set; } = 1;
    /// <summary>Completes when the domain enables and the isolated world are set up. Set by the engine on attach.</summary>
    public Task? Initialization { get; set; }

    /// <summary>What ended the most recent navigation wait: the page event's method name, or "readystate_fallback" if the wait timed out and the page was already loaded.</summary>
    public string? LastNavigationSignal { get; private set; }
    public bool HasOpenDialog => _currentDialogMessage is not null;
    public string? OpenDialogMessage => _currentDialogMessage;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SendCommandAsync("Page.enable", null, ct).ConfigureAwait(false);
        await SendCommandAsync("Runtime.enable", null, ct).ConfigureAwait(false);
        await SendCommandAsync("DOM.enable", null, ct).ConfigureAwait(false);
        // Without this a background or headless page does not fire focus events until it gets window focus,
        // so a page's own onfocus handler could move focus after our focus check. With it, focus is synchronous.
        await SendCommandAsync("Emulation.setFocusEmulationEnabled", new { enabled = true }, ct).ConfigureAwait(false);
        await EnsureIsolatedWorldAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> EnsureIsolatedWorldAsync(CancellationToken ct = default)
    {
        if (_isolatedContextId.HasValue) return _isolatedContextId.Value;

        try
        {
            if (string.IsNullOrEmpty(_rootFrameId))
            {
                var tree = await SendCommandAsync("Page.getFrameTree", null, ct).ConfigureAwait(false);
                if (tree.TryGetProperty("frameTree", out var ft) && ft.TryGetProperty("frame", out var f) && f.TryGetProperty("id", out var idProp))
                {
                    _rootFrameId = idProp.GetString();
                }
            }

            if (string.IsNullOrEmpty(_rootFrameId))
            {
                throw new IsolatedWorldUnavailableException("Could not determine root frameId for isolated world");
            }

            var res = await SendCommandAsync("Page.createIsolatedWorld", new
            {
                frameId = _rootFrameId,
                worldName = "OmniWinIsolatedWorld",
                grantUniveralAccess = false
            }, ct).ConfigureAwait(false);

            if (res.TryGetProperty("executionContextId", out var ctxProp) && ctxProp.TryGetInt32(out var ctxId))
            {
                await SendCommandAsync("Runtime.evaluate", new
                {
                    expression = PageScript.InjectedScript,
                    contextId = ctxId,
                    returnByValue = true
                }, ct).ConfigureAwait(false);

                // Only remember the world once the indexer is installed in it.
                _isolatedContextId = ctxId;
                return ctxId;
            }

            throw new IsolatedWorldUnavailableException("Failed to create isolated world execution context");
        }
        catch (IsolatedWorldUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new IsolatedWorldUnavailableException($"Isolated world is unavailable: {ex.Message}", ex);
        }
    }

    public void HandleEvent(string method, JsonElement @params)
    {
        if (method is "Page.loadEventFired" or "Page.domContentEventFired" or "Page.navigatedWithinDocument")
        {
            _loadTcs.TrySetResult(true);
            lock (_navLock)
            {
                if (_navigationTcs.TrySetResult(true))
                {
                    LastNavigationSignal = method;
                }
            }
        }
        else if (method == "Page.frameNavigated" && @params.TryGetProperty("frame", out var frame))
        {
            if (frame.TryGetProperty("parentId", out var childParent) && childParent.ValueKind != JsonValueKind.Null &&
                frame.TryGetProperty("id", out var childId) && childId.GetString() is { } navigatedChild)
            {
                ForgetFrame(navigatedChild);
            }

            if (!frame.TryGetProperty("parentId", out var parent) || parent.ValueKind == JsonValueKind.Null)
            {
                ForgetAllFrames();
                if (frame.TryGetProperty("id", out var idProp))
                {
                    _rootFrameId = idProp.GetString();
                }
                if (frame.TryGetProperty("url", out var urlProp))
                {
                    CurrentUrl = urlProp.GetString() ?? string.Empty;
                }
                _isolatedContextId = null;
            }
        }
        else if (method == "Runtime.executionContextDestroyed")
        {
            if (@params.TryGetProperty("executionContextId", out var idProp) && idProp.GetInt32() == _isolatedContextId)
            {
                _isolatedContextId = null;
            }
        }
        else if (method == "Page.frameDetached" && @params.TryGetProperty("frameId", out var detachedId) && detachedId.GetString() is { } detached)
        {
            ForgetFrame(detached);
        }
        else if (method == "Runtime.executionContextsCleared")
        {
            ForgetAllFrames();
            _isolatedContextId = null;
        }
        else if (method == "Page.javascriptDialogOpening")
        {
            _currentDialogMessage = @params.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? string.Empty : string.Empty;
            _dialogOpened.TrySetResult();
        }
        else if (method == "Page.javascriptDialogClosed")
        {
            _currentDialogMessage = null;
            _dialogOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Sends an input command. An input event whose handler opens a JavaScript dialog (alert, confirm, prompt) does not
    /// get its CDP response until the dialog is answered, so waiting for it would hang. When a dialog opens first, the
    /// input has been delivered: return, and let the late response (or failure) be ignored.
    /// </summary>
    private async Task SendInputAsync(string method, object parameters, CancellationToken ct)
    {
        var send = SendCommandAsync(method, parameters, ct);
        if (!send.IsCompleted)
        {
            var winner = await Task.WhenAny(send, _dialogOpened.Task).ConfigureAwait(false);
            if (winner != send)
            {
                _ = send.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return;
            }
        }

        await send.ConfigureAwait(false);
    }

    public Task<JsonElement> SendCommandAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        return _connection.SendAsync(method, parameters, SessionId, ct);
    }

    public async Task<string> EvaluateRawAsync(string script, CancellationToken ct = default) =>
        await EvaluateInContextAsync(await EnsureIsolatedWorldAsync(ct).ConfigureAwait(false), script, ct).ConfigureAwait(false);

    private async Task<string> EvaluateInContextAsync(int contextId, string script, CancellationToken ct)
    {
        var result = await SendCommandAsync("Runtime.evaluate", new
        {
            expression = script,
            contextId,
            returnByValue = true,
            awaitPromise = true
        }, ct).ConfigureAwait(false);

        if (result.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var val))
        {
            return val.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>Runs a script in the page's own world. Tests only; the engine never uses the page world.</summary>
    internal async Task<string> EvaluatePageScriptAsync(string script, CancellationToken ct = default)
    {
        var result = await SendCommandAsync("Runtime.evaluate", new
        {
            expression = script,
            returnByValue = true,
            awaitPromise = true
        }, ct).ConfigureAwait(false);

        if (result.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var val))
        {
            return val.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static readonly JsonSerializerOptions ScanJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ElementScanResult> ScanElementsAsync(
        int startRefId,
        int maxElements,
        bool includeText,
        bool viewportOnly,
        CancellationToken ct = default)
    {
        var contextId = await EnsureIsolatedWorldAsync(ct).ConfigureAwait(false);

        var expr = $"window.__omniwin.scan({startRefId}, {maxElements}, {(includeText ? "true" : "false")}, {(viewportOnly ? "true" : "false")}, {RefStride})";
        var res = await SendCommandAsync("Runtime.evaluate", new
        {
            expression = expr,
            contextId,
            returnByValue = true,
            awaitPromise = true
        }, ct).ConfigureAwait(false);

        if (res.TryGetProperty("result", out var outerRes) && outerRes.TryGetProperty("value", out var val))
        {
            var json = val.GetRawText();
            var scan = JsonSerializer.Deserialize<ElementScanResult>(json, ScanJsonOptions)
                ?? throw new InvalidOperationException("Failed to parse DOM scan result");
            CurrentUrl = scan.Url;
            CurrentTitle = scan.Title;
            return scan.Frames == 0 ? scan : await AddFrameElementsAsync(scan, maxElements, includeText, viewportOnly, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Runtime.evaluate returned no value from DOM scan");
    }

    // ---------- frames (Q9): same-origin child frames are indexed, each in its own isolated world ----------

    private sealed record FrameNode(string Id, string? ParentId, string Url, string? Origin);

    private void ForgetFrame(string frameId)
    {
        lock (_frameLock)
        {
            _frameContexts.Remove(frameId);
            foreach (var stale in _refFrames.Where(kv => kv.Value == frameId).Select(kv => kv.Key).ToList())
            {
                _refFrames.Remove(stale);
            }
        }
    }

    private void ForgetAllFrames()
    {
        lock (_frameLock)
        {
            _frameContexts.Clear();
            _refFrames.Clear();
        }
    }

    private string FrameName(string frameId)
    {
        lock (_frameLock)
        {
            if (frameId == _rootFrameId) return "f1";
            if (!_frameNames.TryGetValue(frameId, out var name))
            {
                name = "f" + _nextFrameNumber++;
                _frameNames[frameId] = name;
            }

            return name;
        }
    }

    private async Task<List<FrameNode>> ReadFrameTreeAsync(CancellationToken ct)
    {
        var tree = await SendCommandAsync("Page.getFrameTree", null, ct).ConfigureAwait(false);
        var list = new List<FrameNode>();
        if (tree.TryGetProperty("frameTree", out var rootNode))
        {
            WalkFrames(rootNode, null, list);
        }

        return list;
    }

    private static void WalkFrames(JsonElement node, string? parentId, List<FrameNode> list)
    {
        var frame = node.GetProperty("frame");
        var id = frame.GetProperty("id").GetString()!;
        list.Add(new FrameNode(id, parentId,
            frame.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
            frame.TryGetProperty("securityOrigin", out var o) ? o.GetString() : null));
        if (node.TryGetProperty("childFrames", out var kids) && kids.ValueKind == JsonValueKind.Array)
        {
            foreach (var kid in kids.EnumerateArray())
            {
                WalkFrames(kid, id, list);
            }
        }
    }

    private async Task<int> EnsureFrameWorldAsync(string frameId, CancellationToken ct)
    {
        lock (_frameLock)
        {
            if (_frameContexts.TryGetValue(frameId, out var existing)) return existing;
        }

        var res = await SendCommandAsync("Page.createIsolatedWorld", new
        {
            frameId,
            worldName = "OmniWinIsolatedWorld",
            grantUniveralAccess = false
        }, ct).ConfigureAwait(false);

        if (!res.TryGetProperty("executionContextId", out var ctxProp) || !ctxProp.TryGetInt32(out var ctxId))
        {
            throw new IsolatedWorldUnavailableException("Failed to create an isolated world for a child frame");
        }

        await SendCommandAsync("Runtime.evaluate", new { expression = PageScript.InjectedScript, contextId = ctxId, returnByValue = true }, ct).ConfigureAwait(false);
        lock (_frameLock)
        {
            _frameContexts[frameId] = ctxId;
        }

        return ctxId;
    }

    /// <summary>Top-left of a frame's content box in page coordinates (to turn frame-relative element coordinates into page coordinates).</summary>
    private async Task<(double X, double Y)> FrameOffsetAsync(string frameId, CancellationToken ct)
    {
        var owner = await SendCommandAsync("DOM.getFrameOwner", new { frameId }, ct).ConfigureAwait(false);
        var backendNodeId = owner.GetProperty("backendNodeId").GetInt32();
        var box = await SendCommandAsync("DOM.getBoxModel", new { backendNodeId }, ct).ConfigureAwait(false);
        var content = box.GetProperty("model").GetProperty("content");
        return (content[0].GetDouble(), content[1].GetDouble());
    }

    private static string OriginOnly(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? $"{uri.Scheme}://{uri.Authority}" : "unknown";

    /// <summary>
    /// Same-origin frames whose contents are missing from this observation: those the page reports as still loading plus those
    /// whose read failed, never more than the number of same-origin frames.
    /// </summary>
    internal static int FramesNotReadCount(IReadOnlyList<FrameDetail> details, int failedReads)
    {
        var sameOrigin = details.Count(d => d.Accessible);
        var loading = details.Count(d => d.Accessible && d.Loading);
        return Math.Min(sameOrigin, loading + Math.Max(0, failedReads));
    }

    private async Task<ElementScanResult> AddFrameElementsAsync(
        ElementScanResult page, int maxElements, bool includeText, bool viewportOnly, CancellationToken ct)
    {
        var nodes = await ReadFrameTreeAsync(ct).ConfigureAwait(false);
        var rootNode = nodes.FirstOrDefault(n => n.ParentId is null);
        if (rootNode is null) return page;

        var elements = page.Elements.ToList();
        var next = page.NextRefId;
        var omittedOffscreen = page.OmittedOffscreen;
        var omittedNonInteractive = page.OmittedNonInteractive;
        var cross = new List<CrossOriginFrame>();
        var failedReads = 0;

        // Direct children of the page only; deeper frames are counted in frames=N but not indexed.
        foreach (var child in nodes.Where(n => n.ParentId == rootNode.Id))
        {
            var name = FrameName(child.Id);
            var sameOrigin = !string.IsNullOrEmpty(rootNode.Origin) && rootNode.Origin != "://" && child.Origin == rootNode.Origin;
            if (!sameOrigin) continue;   // listed below from the page's own view of its iframes

            var remaining = maxElements - elements.Count;
            if (remaining <= 0) break;

            try
            {
                var contextId = await EnsureFrameWorldAsync(child.Id, ct).ConfigureAwait(false);
                var offset = await FrameOffsetAsync(child.Id, ct).ConfigureAwait(false);
                var expr = $"window.__omniwin.scan({next}, {remaining}, {(includeText ? "true" : "false")}, {(viewportOnly ? "true" : "false")}, {RefStride})";
                var res = await SendCommandAsync("Runtime.evaluate", new { expression = expr, contextId, returnByValue = true, awaitPromise = true }, ct).ConfigureAwait(false);
                if (!res.TryGetProperty("result", out var outer) || !outer.TryGetProperty("value", out var val))
                {
                    failedReads++;
                    continue;
                }

                var inner = JsonSerializer.Deserialize<ElementScanResult>(val.GetRawText(), ScanJsonOptions);
                if (inner is null)
                {
                    failedReads++;
                    continue;
                }

                lock (_frameLock)
                {
                    foreach (var el in inner.Elements)
                    {
                        _refFrames[el.Ref] = child.Id;
                    }
                }

                foreach (var el in inner.Elements)
                {
                    elements.Add(el with
                    {
                        Frame = name,
                        Bounds = new ElementBounds(el.Bounds.X + offset.X, el.Bounds.Y + offset.Y, el.Bounds.Width, el.Bounds.Height),
                        Center = new ElementCenter(el.Center.X + offset.X, el.Center.Y + offset.Y)
                    });
                }

                next = Math.Max(next, inner.NextRefId);
                omittedOffscreen += inner.OmittedOffscreen;
                omittedNonInteractive += inner.OmittedNonInteractive;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The frame went away or navigated while it was being read: it contributes no elements this time, and the
                // footer says so (frames_not_read) so the model knows to observe again.
                failedReads++;
            }
        }

        // Cross-origin frames may live in another process and then are not in this page's frame tree at all, so they are
        // listed from the page's own iframe elements whose document cannot be read.
        var details = page.FrameDetails ?? [];
        for (var i = 0; i < details.Count; i++)
        {
            if (!details[i].Accessible)
            {
                cross.Add(new CrossOriginFrame(FrameName($"dom:{i}:{details[i].Src}"), OriginOnly(details[i].Src)));
            }
        }

        return page with
        {
            Elements = elements,
            NextRefId = next,
            OmittedOffscreen = omittedOffscreen,
            OmittedNonInteractive = omittedNonInteractive,
            CrossOriginFrames = cross.Count > 0 ? cross : null,
            FramesNotRead = FramesNotReadCount(details, failedReads)
        };
    }

    // ---------- browser_view: a masked screenshot (look-only) ----------

    private sealed record SensitiveRect(double X, double Y, double W, double H);

    private sealed record SensitiveReply(List<SensitiveRect> Rects, double ScrollX, double ScrollY, double Dpr, string? Title);

    /// <summary>A screenshot (PNG) with the rectangles that must be black before it leaves, in image pixels, and the page title.</summary>
    public sealed record ViewCapture(byte[] Png, IReadOnlyList<PixelRect> Masks, string Title);

    /// <summary>
    /// Takes a screenshot of the visible viewport (or the whole page) and finds every password and payment field,
    /// every cross-origin frame included whole. The sensitive areas are read before and after the screenshot and both
    /// sets are masked, so a page that moves between the two reads still can't leak a field. Any failure throws, and the
    /// caller returns no image.
    /// </summary>
    public async Task<ViewCapture> CaptureViewAsync(bool fullPage, CancellationToken ct = default)
    {
        var before = await ReadSensitiveAsync(ct).ConfigureAwait(false);

        double pixelsPerCssPixel;
        object parameters;
        if (fullPage)
        {
            var metrics = await SendCommandAsync("Page.getLayoutMetrics", null, ct).ConfigureAwait(false);
            var content = metrics.GetProperty("cssContentSize");
            var width = Math.Clamp(content.GetProperty("width").GetDouble(), 1, 8000);
            var height = Math.Clamp(content.GetProperty("height").GetDouble(), 1, 16000);
            parameters = new { format = "png", fromSurface = true, captureBeyondViewport = true, clip = new { x = 0, y = 0, width, height, scale = 1 } };
            pixelsPerCssPixel = 1;
        }
        else
        {
            parameters = new { format = "png", fromSurface = true };
            pixelsPerCssPixel = before.Dpr > 0 ? before.Dpr : 1;
        }

        var shot = await SendCommandAsync("Page.captureScreenshot", parameters, ct).ConfigureAwait(false);
        var png = Convert.FromBase64String(shot.GetProperty("data").GetString() ?? throw new InvalidOperationException("The screenshot had no data."));
        var after = await ReadSensitiveAsync(ct).ConfigureAwait(false);

        var masks = new List<PixelRect>();
        foreach (var reply in new[] { before, after })
        {
            var offsetX = fullPage ? reply.ScrollX : 0;
            var offsetY = fullPage ? reply.ScrollY : 0;
            foreach (var r in reply.Rects)
            {
                // 2 CSS pixels of padding on every side.
                masks.Add(new PixelRect(
                    (int)Math.Floor((r.X + offsetX - 2) * pixelsPerCssPixel),
                    (int)Math.Floor((r.Y + offsetY - 2) * pixelsPerCssPixel),
                    (int)Math.Ceiling((r.W + 4) * pixelsPerCssPixel),
                    (int)Math.Ceiling((r.H + 4) * pixelsPerCssPixel)));
            }
        }

        return new ViewCapture(png, masks, before.Title ?? after.Title ?? string.Empty);
    }

    private async Task<SensitiveReply> ReadSensitiveAsync(CancellationToken ct)
    {
        var contextId = await EnsureIsolatedWorldAsync(ct).ConfigureAwait(false);
        var page = await EvaluateSensitiveAsync(contextId, ct).ConfigureAwait(false);

        // Closed shadow roots are invisible to page script but not to CDP: any that holds an input is blacked out whole.
        var closed = await ReadClosedShadowHostsAsync(ct).ConfigureAwait(false);
        if (closed.Count > 0) page = page with { Rects = page.Rects.Concat(closed).ToList() };

        if (page.Rects.Count == 0 && !await HasFramesAsync(contextId, ct).ConfigureAwait(false)) return page;

        // Same-origin child frames: their fields, shifted into page (viewport) coordinates.
        var nodes = await ReadFrameTreeAsync(ct).ConfigureAwait(false);
        var rootNode = nodes.FirstOrDefault(n => n.ParentId is null);
        if (rootNode is null) return page;

        var rects = new List<SensitiveRect>(page.Rects);
        foreach (var child in nodes.Where(n => n.ParentId == rootNode.Id))
        {
            var sameOrigin = !string.IsNullOrEmpty(rootNode.Origin) && rootNode.Origin != "://" && child.Origin == rootNode.Origin;
            if (!sameOrigin) continue;   // a cross-origin frame is already in the list whole, from the page's own view

            var frameContext = await EnsureFrameWorldAsync(child.Id, ct).ConfigureAwait(false);
            var inner = await EvaluateSensitiveAsync(frameContext, ct, maskAllFrames: true).ConfigureAwait(false);
            var offset = await FrameOffsetAsync(child.Id, ct).ConfigureAwait(false);
            rects.AddRange(inner.Rects.Select(r => new SensitiveRect(r.X + offset.X, r.Y + offset.Y, r.W, r.H)));
        }

        return page with { Rects = rects };
    }

    /// <summary>
    /// Walks the whole DOM through CDP (shadow roots included, open or closed) and returns the page rectangles of every
    /// host of a closed shadow root that contains an input or textarea, and of every child frame whose document contains
    /// one. Those are blacked out whole because their fields can't be told apart.
    /// </summary>
    private async Task<List<SensitiveRect>> ReadClosedShadowHostsAsync(CancellationToken ct)
    {
        var doc = await SendCommandAsync("DOM.getDocument", new { depth = -1, pierce = true }, ct).ConfigureAwait(false);
        var hosts = new List<int>();
        CollectClosedHosts(doc.GetProperty("root"), hosts);

        var rects = new List<SensitiveRect>();
        foreach (var backendNodeId in hosts)
        {
            var box = await SendCommandAsync("DOM.getBoxModel", new { backendNodeId }, ct).ConfigureAwait(false);
            var q = box.GetProperty("model").GetProperty("border");
            double[] xs = [q[0].GetDouble(), q[2].GetDouble(), q[4].GetDouble(), q[6].GetDouble()];
            double[] ys = [q[1].GetDouble(), q[3].GetDouble(), q[5].GetDouble(), q[7].GetDouble()];
            var x = xs.Min();
            var y = ys.Min();
            rects.Add(new SensitiveRect(x, y, xs.Max() - x, ys.Max() - y));
        }

        return rects;
    }

    private static void CollectClosedHosts(JsonElement node, List<int> hosts)
    {
        var backend = node.TryGetProperty("backendNodeId", out var b) ? b.GetInt32() : 0;
        var isFrame = node.TryGetProperty("nodeName", out var nn) && nn.GetString() is "IFRAME" or "FRAME";

        if (isFrame && node.TryGetProperty("contentDocument", out var content))
        {
            // A child frame is searched by its own world; but if it holds a closed shadow root with an input, mask it whole.
            if (backend != 0 && HoldsClosedShadowWithInput(content)) hosts.Add(backend);
            return;
        }

        if (node.TryGetProperty("shadowRoots", out var roots) && roots.ValueKind == JsonValueKind.Array)
        {
            foreach (var shadow in roots.EnumerateArray())
            {
                var closed = shadow.TryGetProperty("shadowRootType", out var t) && t.GetString() == "closed";
                if (closed && backend != 0 && ContainsInput(shadow)) hosts.Add(backend);
                CollectClosedHosts(shadow, hosts);   // closed roots nested in either kind of root
            }
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                CollectClosedHosts(child, hosts);
            }
        }
    }

    private static bool ContainsInput(JsonElement node)
    {
        if (node.TryGetProperty("nodeName", out var nn) && nn.GetString() is "INPUT" or "TEXTAREA") return true;
        foreach (var name in new[] { "children", "shadowRoots" })
        {
            if (node.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array &&
                list.EnumerateArray().Any(ContainsInput))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HoldsClosedShadowWithInput(JsonElement node)
    {
        if (node.TryGetProperty("shadowRoots", out var roots) && roots.ValueKind == JsonValueKind.Array)
        {
            foreach (var shadow in roots.EnumerateArray())
            {
                if (shadow.TryGetProperty("shadowRootType", out var t) && t.GetString() == "closed" && ContainsInput(shadow)) return true;
                if (HoldsClosedShadowWithInput(shadow)) return true;
            }
        }

        foreach (var name in new[] { "children" })
        {
            if (node.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array &&
                list.EnumerateArray().Any(HoldsClosedShadowWithInput))
            {
                return true;
            }
        }

        if (node.TryGetProperty("contentDocument", out var inner) && HoldsClosedShadowWithInput(inner)) return true;
        return false;
    }

    private async Task<bool> HasFramesAsync(int contextId, CancellationToken ct)
    {
        var raw = await EvaluateInContextAsync(contextId, "document.querySelectorAll('iframe,frame').length", ct).ConfigureAwait(false);
        return int.TryParse(raw, out var count) && count > 0;
    }

    private async Task<SensitiveReply> EvaluateSensitiveAsync(int contextId, CancellationToken ct, bool maskAllFrames = false)
    {
        var res = await SendCommandAsync("Runtime.evaluate", new
        {
            expression = maskAllFrames ? "window.__omniwin.sensitiveRects(true)" : "window.__omniwin.sensitiveRects(false)",
            contextId,
            returnByValue = true,
            awaitPromise = true
        }, ct).ConfigureAwait(false);

        if (!res.TryGetProperty("result", out var outer) || !outer.TryGetProperty("value", out var value))
        {
            throw new InvalidOperationException("The page did not report its sensitive fields.");
        }

        return JsonSerializer.Deserialize<SensitiveReply>(value.GetRawText(), ScanJsonOptions)
            ?? throw new InvalidOperationException("The page's sensitive fields could not be read.");
    }

    /// <summary>The isolated world a ref lives in: the page's, or the child frame's that the ref was found in.</summary>
    private async Task<(int ContextId, string? FrameId)> WorldForRefAsync(string refId, CancellationToken ct)
    {
        string? frameId;
        lock (_frameLock)
        {
            _refFrames.TryGetValue(refId, out frameId);
        }

        return frameId is null
            ? (await EnsureIsolatedWorldAsync(ct).ConfigureAwait(false), null)
            : (await EnsureFrameWorldAsync(frameId, ct).ConfigureAwait(false), frameId);
    }

    public TaskCompletionSource<bool> PrepareForNavigation()
    {
        lock (_navLock)
        {
            _navigationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _navigationTcs;
        }
    }

    public async Task WaitForNavigationAsync(TaskCompletionSource<bool> navTcs, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await navTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                var readyState = await EvaluateRawAsync("document.readyState", ct).ConfigureAwait(false);
                if (readyState is "interactive" or "complete")
                {
                    LastNavigationSignal = "readystate_fallback";
                    return;
                }
            }
            catch
            {
                // Ignore fallback error
            }
            throw new TimeoutException($"Navigation wait timed out after {timeout.TotalMilliseconds}ms");
        }
    }

    public async Task NavigateAsync(string url, Action? onInputSent = null, CancellationToken ct = default)
    {
        var navTcs = PrepareForNavigation();
        onInputSent?.Invoke();
        await SendCommandAsync("Page.navigate", new { url }, ct).ConfigureAwait(false);
        await WaitForNavigationAsync(navTcs, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        await EnsureIsolatedWorldAsync(ct).ConfigureAwait(false);
    }

    public async Task ClickAsync(double x, double y, Action? onInputSent = null, CancellationToken ct = default)
    {
        onInputSent?.Invoke();
        await SendInputAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseMoved",
            x,
            y
        }, ct).ConfigureAwait(false);

        await SendInputAsync("Input.dispatchMouseEvent", new
        {
            type = "mousePressed",
            x,
            y,
            button = "left",
            clickCount = 1
        }, ct).ConfigureAwait(false);

        await SendInputAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseReleased",
            x,
            y,
            button = "left",
            clickCount = 1
        }, ct).ConfigureAwait(false);
    }

    public async Task DoubleClickAsync(double x, double y, Action? onInputSent = null, CancellationToken ct = default)
    {
        await ClickAsync(x, y, onInputSent, ct).ConfigureAwait(false);
        await SendInputAsync("Input.dispatchMouseEvent", new
        {
            type = "mousePressed",
            x,
            y,
            button = "left",
            clickCount = 2
        }, ct).ConfigureAwait(false);

        await SendInputAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseReleased",
            x,
            y,
            button = "left",
            clickCount = 2
        }, ct).ConfigureAwait(false);
    }

    public async Task HoverAsync(double x, double y, Action? onInputSent = null, CancellationToken ct = default)
    {
        onInputSent?.Invoke();
        await SendInputAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseMoved",
            x,
            y
        }, ct).ConfigureAwait(false);
    }

    public async Task InsertTextAsync(string text, Action? onInputSent = null, CancellationToken ct = default)
    {
        onInputSent?.Invoke();
        await SendInputAsync("Input.insertText", new { text }, ct).ConfigureAwait(false);
    }

    public async Task PressKeyAsync(string key, Action? onInputSent = null, CancellationToken ct = default)
    {
        var (windowsVirtualKeyCode, domKey, code, text) = MapKey(key);
        onInputSent?.Invoke();

        // Keys that produce text (Enter, Space) are sent as keyDown with text so the page sees a real key press
        // (form submit on Enter, character insertion on Space). Other keys are sent as rawKeyDown.
        if (text is null)
        {
            await SendInputAsync("Input.dispatchKeyEvent", new
            {
                type = "rawKeyDown",
                windowsVirtualKeyCode,
                key = domKey,
                code
            }, ct).ConfigureAwait(false);
        }
        else
        {
            await SendInputAsync("Input.dispatchKeyEvent", new
            {
                type = "keyDown",
                windowsVirtualKeyCode,
                key = domKey,
                code,
                text
            }, ct).ConfigureAwait(false);
        }

        await SendInputAsync("Input.dispatchKeyEvent", new
        {
            type = "keyUp",
            windowsVirtualKeyCode,
            key = domKey,
            code
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Accepts or dismisses the open JavaScript dialog (Q8). The page does not run until it is handled.</summary>
    public async Task HandleDialogAsync(bool accept, CancellationToken ct = default)
    {
        await SendCommandAsync("Page.handleJavaScriptDialog", new { accept }, ct).ConfigureAwait(false);
        _currentDialogMessage = null;
        _dialogOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Number of frames in the page, the root included (the <c>frames=N</c> in the observation header).</summary>
    public async Task<int> CountFramesAsync(CancellationToken ct = default)
    {
        var tree = await SendCommandAsync("Page.getFrameTree", null, ct).ConfigureAwait(false);
        return tree.TryGetProperty("frameTree", out var root) ? CountFrames(root) : 1;
    }

    private static int CountFrames(JsonElement node)
    {
        var count = 1;
        if (node.TryGetProperty("childFrames", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                count += CountFrames(child);
            }
        }

        return count;
    }

    public async Task<LiveElementInspection> InspectElementAsync(string refId, CancellationToken ct = default)
    {
        var (contextId, frameId) = await WorldForRefAsync(refId, ct).ConfigureAwait(false);
        var sanitizedRef = JsonSerializer.Serialize(refId);
        var expr = $"window.__omniwin.inspectElement({sanitizedRef})";
        var res = await SendCommandAsync("Runtime.evaluate", new
        {
            expression = expr,
            contextId,
            returnByValue = true,
            awaitPromise = true
        }, ct).ConfigureAwait(false);

        if (res.TryGetProperty("result", out var outerRes) && outerRes.TryGetProperty("value", out var val))
        {
            var json = val.GetRawText();
            var live = JsonSerializer.Deserialize<LiveElementInspection>(json, ScanJsonOptions) ?? new LiveElementInspection(false);
            return frameId is not null && live.Found ? await ToPageCoordinatesAsync(live, frameId, ct).ConfigureAwait(false) : live;
        }

        return new LiveElementInspection(false);
    }

    /// <summary>Moves a frame element's bounds and click point into page coordinates and checks, at page level, that the frame itself is what is at that point.</summary>
    private async Task<LiveElementInspection> ToPageCoordinatesAsync(LiveElementInspection live, string frameId, CancellationToken ct)
    {
        if (live.Bounds is null || live.Center is null) return live;

        var offset = await FrameOffsetAsync(frameId, ct).ConfigureAwait(false);
        var center = new ElementCenter(live.Center.X + offset.X, live.Center.Y + offset.Y);
        var moved = live with
        {
            Bounds = new ElementBounds(live.Bounds.X + offset.X, live.Bounds.Y + offset.Y, live.Bounds.Width, live.Bounds.Height),
            Center = center
        };

        if (!moved.IsExactHit) return moved;

        try
        {
            var at = await SendCommandAsync("DOM.getNodeForLocation",
                new { x = (int)Math.Round(center.X), y = (int)Math.Round(center.Y), includeUserAgentShadowDOM = false }, ct).ConfigureAwait(false);
            var hitFrame = at.TryGetProperty("frameId", out var f) ? f.GetString() : null;
            if (hitFrame != frameId)
            {
                return moved with { IsExactHit = false, Occlusion = moved.Occlusion ?? ReasonCodes.OccludedSameWindow };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return moved with { IsExactHit = false, Occlusion = moved.Occlusion ?? ReasonCodes.OccludedSameWindow };
        }

        return moved;
    }

    public async Task<PageActionResult> ExecuteInPageActionAsync(string refId, string action, string? value = null, Action? onInputSent = null, CancellationToken ct = default)
    {
        onInputSent?.Invoke();
        var sanitizedRef = JsonSerializer.Serialize(refId);
        var sanitizedAction = JsonSerializer.Serialize(action);
        var sanitizedVal = value is not null ? JsonSerializer.Serialize(value) : "null";

        var expr = $"window.__omniwin.executeAction({sanitizedRef}, {sanitizedAction}, {sanitizedVal})";
        var res = await EvaluateInContextAsync((await WorldForRefAsync(refId, ct).ConfigureAwait(false)).ContextId, expr, ct).ConfigureAwait(false);
        return ParsePageActionResult(res);
    }

    /// <summary>
    /// Asks the isolated world whether this exact ref is the document's active element. Called as late as possible
    /// before key or text input so the input cannot land on a different element.
    /// </summary>
    public async Task<PageActionResult> IsFocusedAsync(string refId, CancellationToken ct = default)
    {
        var expr = $"window.__omniwin.isFocused({JsonSerializer.Serialize(refId)})";
        var res = await EvaluateInContextAsync((await WorldForRefAsync(refId, ct).ConfigureAwait(false)).ContextId, expr, ct).ConfigureAwait(false);
        return ParsePageActionResult(res);
    }

    private static PageActionResult ParsePageActionResult(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("success", out var ok) &&
                ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                return new PageActionResult(ok.GetBoolean(), reason);
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return new PageActionResult(false, "bad_result");
    }

    private static (int VKey, string DomKey, string Code, string? Text) MapKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "enter" or "return" => (13, "Enter", "Enter", "\r"),
            "tab" => (9, "Tab", "Tab", null),
            "escape" or "esc" => (27, "Escape", "Escape", null),
            "backspace" => (8, "Backspace", "Backspace", null),
            "arrowdown" or "down" => (40, "ArrowDown", "ArrowDown", null),
            "arrowup" or "up" => (38, "ArrowUp", "ArrowUp", null),
            "arrowleft" or "left" => (37, "ArrowLeft", "ArrowLeft", null),
            "arrowright" or "right" => (39, "ArrowRight", "ArrowRight", null),
            "space" or " " => (32, " ", "Space", " "),
            _ => (0, key, key, null)
        };
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
