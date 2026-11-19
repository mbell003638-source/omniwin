using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using OmniWin.Core;
using OmniWin.Safety;
using static Interop.UIAutomationClient.UIA_ControlTypeIds;
using static Interop.UIAutomationClient.UIA_PatternIds;
using static Interop.UIAutomationClient.UIA_PropertyIds;

namespace OmniWin.Desktop;

internal sealed partial class UiaDesktop
{
    private const int MaxSnapshotElements = 1000;
    private const int MaxScannedElements = 4000;
    private const int MaxObservationValue = 4096;
    private const int ProcessCacheLimit = 128;
    private readonly IUIAutomation _uia;
    private readonly IUIAutomationTreeWalker _walker;
    private readonly IUIAutomationTreeWalker _rawWalker;
    private readonly IUIAutomationCacheRequest _windowCache;
    private readonly IUIAutomationCacheRequest _elementCache;
    private readonly UiaEvents _events;
    private readonly OmniWinConfig _config;
    private readonly IOcrRefResolver? _ocr;
    private readonly IWindowTextReader? _screenText;
    private readonly HashSet<string> _blockedProcesses;
    private readonly DesktopIndex _index = new();
    private readonly Dictionary<string, WindowRead> _reads = new(StringComparer.Ordinal);
    private readonly Dictionary<long, WindowMetadata> _windowMetadata = new();
    private readonly Dictionary<int, CachedProcess> _processes = new();
    private readonly SlowWindowTracker _slowWindows = new();
    private bool _onInputDesktop;
    private long _windowsReadAt;
    private long _uiaWindowsReadAt;
    private long _windowEventVersion = -1;
    private bool _windowsInitialized;

    internal UiaDesktop(IUIAutomation uia, UiaEvents events, OmniWinConfig config, IOcrRefResolver? ocr,
        IWindowTextReader? screenText = null)
    {
        _uia = uia;
        _events = events;
        _config = config;
        _ocr = ocr;
        _screenText = screenText;
        _blockedProcesses = new HashSet<string>(DeletePolicy.DefaultBlockedProcesses, StringComparer.OrdinalIgnoreCase);
        _blockedProcesses.UnionWith(config.ProcessBlocklistExtra);
        _blockedProcesses.UnionWith(DeletePolicy.OwnProcesses);
        _walker = uia.ControlViewWalker;
        _rawWalker = uia.RawViewWalker;
        _windowCache = CreateCache(uia.RawViewCondition,
            [UIA_NativeWindowHandlePropertyId, UIA_NamePropertyId, UIA_WindowIsModalPropertyId]);
        _elementCache = CreateCache(uia.RawViewCondition,
            [UIA_RuntimeIdPropertyId, UIA_ProcessIdPropertyId, UIA_ControlTypePropertyId, UIA_NamePropertyId,
             UIA_AutomationIdPropertyId, UIA_ClassNamePropertyId, UIA_BoundingRectanglePropertyId, UIA_IsOffscreenPropertyId,
             UIA_NativeWindowHandlePropertyId,
             UIA_IsPasswordPropertyId, UIA_IsEnabledPropertyId, UIA_HasKeyboardFocusPropertyId, UIA_IsKeyboardFocusablePropertyId,
             UIA_IsControlElementPropertyId,
             UIA_IsRequiredForFormPropertyId, UIA_IsDataValidForFormPropertyId, UIA_IsInvokePatternAvailablePropertyId,
             UIA_IsTogglePatternAvailablePropertyId, UIA_IsValuePatternAvailablePropertyId, UIA_IsTextPatternAvailablePropertyId,
             UIA_IsSelectionItemPatternAvailablePropertyId, UIA_IsExpandCollapsePatternAvailablePropertyId,
             UIA_IsScrollItemPatternAvailablePropertyId, UIA_IsScrollPatternAvailablePropertyId, UIA_ValueIsReadOnlyPropertyId,
             UIA_ToggleToggleStatePropertyId, UIA_SelectionItemIsSelectedPropertyId, UIA_ExpandCollapseExpandCollapseStatePropertyId,
             UIA_ScrollHorizontalScrollPercentPropertyId, UIA_ScrollVerticalScrollPercentPropertyId]);
    }

    private IUIAutomationCacheRequest CreateCache(IUIAutomationCondition filter, int[] properties)
    {
        var cache = _uia.CreateCacheRequest();
        cache.TreeFilter = filter;
        cache.TreeScope = TreeScope.TreeScope_Element;
        foreach (var property in properties)
        {
            cache.AddProperty(property);
        }

        return cache;
    }

    public IReadOnlyList<WindowInfo> ListWindows(CancellationToken ct)
    {
        RefreshWindows(ct);
        return _index.Windows.Select(window => window.Info).ToArray();
    }

    private void RefreshWindows(CancellationToken ct, bool fresh = false)
    {
        using var diagnostic = DesktopDiagnostics.Measure("RefreshWindows");
        ct.ThrowIfCancellationRequested();
        _onInputDesktop = NativeDesktop.OnInputDesktop();
        var foreground = NativeDesktop.Foreground;
        var eventVersion = _events.WindowVersion;
        if (!_onInputDesktop)
        {
            // Preserve identities across a desktop switch without querying providers on the inactive desktop.
            _index.UpdateWindows(_index.Windows.Select(window => window.Sample with
                { Foreground = false, Inaccessible = "secure_desktop" }).ToArray());
            return;
        }

        if (!fresh && _windowsInitialized && _windowEventVersion == eventVersion &&
            Stopwatch.GetElapsedTime(_windowsReadAt).TotalMilliseconds < (_events.Available ? 500 : 25) &&
            RefreshKnownWindows(foreground))
        {
            return;
        }

        var samples = new List<WindowSample>(_index.Windows.Count);
        var handles = new HashSet<long>();
        if (ShouldScanWindowUia(fresh, _windowsInitialized, _events.Available,
                _windowEventVersion != eventVersion, Stopwatch.GetElapsedTime(_uiaWindowsReadAt)))
        {
            // EnumWindows enumerates desktop apps only on Windows 8+. Discover shell/UWP surfaces
            // immediately on healthy events or explicit refreshes. Missing-event fallback scans
            // are capped at once per second; native inventory still refreshes at its own cadence.
            using var fullScanDiagnostic = DesktopDiagnostics.Measure("FullWindowUiaScan");
            var windows = _uia.GetRootElement().FindAllBuildCache(TreeScope.TreeScope_Children, _uia.RawViewCondition, _windowCache);
            for (var i = 0; i < windows.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var element = windows.GetElement(i);
                    var hwnd = (long)element.CachedNativeWindowHandle;
                    if (!handles.Contains(hwnd) && ReadWindow(hwnd, foreground, eventVersion, element) is { } sample)
                    {
                        handles.Add(hwnd);
                        samples.Add(sample);
                    }
                }
                catch (COMException ex) when (ElementUnavailable(ex))
                {
                    // The window closed while UIA was returning the desktop array.
                }
            }

            _uiaWindowsReadAt = Stopwatch.GetTimestamp();
        }

        // A focused shell surface can be nested in UIA and omitted by EnumWindows. Retain previously
        // discovered HWNDs too; ReadWindow still applies fresh visibility, ownership, and process checks.
        foreach (var hwnd in DiscoveryCandidates(NativeDesktop.TopLevelWindows(ct), NativeDesktop.Root(foreground),
                     _index.Windows.Select(window => window.Sample.Hwnd)))
        {
            ct.ThrowIfCancellationRequested();
            if (handles.Contains(hwnd)) continue;
            if (ReadWindow(hwnd, foreground, eventVersion) is { } sample)
            {
                handles.Add(hwnd);
                samples.Add(sample);
            }
        }

        _index.UpdateWindows(samples);
        foreach (var id in _reads.Keys)
        {
            if (_index.ById(id) is null)
            {
                _reads.Remove(id);
            }
        }

        foreach (var hwnd in _windowMetadata.Keys)
        {
            if (!handles.Contains(hwnd)) _windowMetadata.Remove(hwnd);
        }

        _slowWindows.Retain(handles);

        _windowsInitialized = true;
        _windowEventVersion = eventVersion;
        _windowsReadAt = Stopwatch.GetTimestamp();
    }

    internal static IEnumerable<long> DiscoveryCandidates(IEnumerable<long> enumerated, long foregroundRoot,
        IEnumerable<long> known)
    {
        var seen = new HashSet<long>();
        foreach (var hwnd in enumerated.Concat([foregroundRoot]).Concat(known))
        {
            if (hwnd != 0 && seen.Add(hwnd)) yield return hwnd;
        }
    }

    internal static bool ShouldScanWindowUia(bool fresh, bool initialized, bool eventsAvailable,
        bool windowEventChanged, TimeSpan sinceFullScan) =>
        fresh || !initialized || (eventsAvailable
            ? windowEventChanged
            : sinceFullScan >= TimeSpan.FromSeconds(1));

    private WindowSample? ReadWindow(long hwnd, long foreground, long eventVersion, IUIAutomationElement? initial = null)
    {
        if (!NativeDesktop.IsTopLevelWindow(hwnd)) return null;
        var pid = NativeDesktop.ProcessId(hwnd);
        if (pid <= 0) return null;
        var process = ProcessInfo(pid);
        var title = NativeDesktop.Title(hwnd);
        var owner = NativeDesktop.Owner(hwnd);
        var inaccessible = AccessState(hwnd, pid, process);
        WindowMetadata? metadata = null;
        if (inaccessible is null || initial is not null)
        {
            // D4: a window whose provider is slow gets its own time limit. A read that times out or takes too long
            // marks the window slow; it is then listed as inaccessible=slow and not queried again until its cool-down ends.
            var readStart = Stopwatch.GetTimestamp();
            try
            {
                metadata = ReadWindowMetadata(hwnd, pid, process.Start, title, owner, eventVersion, initial);
            }
            catch (COMException ex) when (UiaTimedOut(ex))
            {
                _slowWindows.Mark(hwnd, pid, process.Start);
                _windowMetadata.Remove(hwnd);
                inaccessible ??= "slow";
            }

            if (inaccessible is null)
            {
                _slowWindows.Observe(hwnd, pid, process.Start, Stopwatch.GetElapsedTime(readStart));
            }

            if (metadata is null && inaccessible is null) return null;
        }
        else if (_windowMetadata.TryGetValue(hwnd, out var cached) && cached.Pid == pid && cached.ProcessStart == process.Start)
        {
            metadata = cached;
        }

        if (NativeDesktop.ProcessId(hwnd) != pid) return null;
        if (title.Length == 0) title = metadata?.Name ?? string.Empty;
        var modal = metadata?.Modal == true || owner != 0 && !NativeDesktop.Enabled(owner);
        return new WindowSample(hwnd, pid, process.Start, process.Name, title, NativeDesktop.Bounds(hwnd),
            hwnd == foreground, NativeDesktop.Minimized(hwnd), modal, owner, inaccessible);
    }

    private WindowMetadata? ReadWindowMetadata(long hwnd, int pid, long processStart, string nativeTitle, long owner,
        long eventVersion, IUIAutomationElement? initial)
    {
        var modalVersion = _events.ModalVersion;
        var now = Stopwatch.GetTimestamp();
        _windowMetadata.TryGetValue(hwnd, out var previous);
        if (previous is not null && (previous.Pid != pid || previous.ProcessStart != processStart))
        {
            _windowMetadata.Remove(hwnd);
            previous = null;
        }

        if (initial is null && previous is not null && previous.ModalVersion == modalVersion && previous.Owner == owner &&
            (nativeTitle.Length != 0 || previous.EventVersion == eventVersion) &&
            (owner == 0 && nativeTitle.Length != 0 || Stopwatch.GetElapsedTime(previous.ReadAt, now).TotalMilliseconds < 500))
        {
            return previous;
        }

        try
        {
            var element = initial;
            if (element is null && previous is not null)
            {
                try
                {
                    element = previous.Element.BuildUpdatedCache(_windowCache);
                }
                catch (COMException ex) when (ElementUnavailable(ex))
                {
                    // A provider can replace its window element while the native window remains alive.
                }
            }

            element ??= _uia.ElementFromHandleBuildCache((nint)hwnd, _windowCache);
            if ((long)element.CachedNativeWindowHandle != hwnd)
            {
                _windowMetadata.Remove(hwnd);
                return null;
            }
            var current = new WindowMetadata(pid, processStart, owner, element, element.CachedName ?? string.Empty,
                IsTrue(element, UIA_WindowIsModalPropertyId), eventVersion, modalVersion, now);
            _windowMetadata[hwnd] = current;
            return current;
        }
        catch (COMException ex) when (ElementUnavailable(ex))
        {
            _windowMetadata.Remove(hwnd);
            return null;
        }
    }

    private bool RefreshKnownWindows(long foreground)
    {
        var samples = new List<WindowSample>(_index.Windows.Count);
        foreach (var window in _index.Windows)
        {
            var previous = window.Sample;
            if (!NativeDesktop.IsTopLevelWindow(previous.Hwnd) || NativeDesktop.ProcessId(previous.Hwnd) != previous.Pid)
            {
                return false;
            }

            var process = ProcessInfo(previous.Pid);
            if (process.Start != previous.ProcessStart || process.Name != previous.Process)
            {
                return false;
            }

            if (AccessState(previous.Hwnd, previous.Pid, process) is null && !_windowMetadata.ContainsKey(previous.Hwnd))
            {
                return false;
            }

            var owner = NativeDesktop.Owner(previous.Hwnd);
            var title = NativeDesktop.Title(previous.Hwnd);
            if (title.Length == 0 && _windowMetadata.TryGetValue(previous.Hwnd, out var metadata))
            {
                title = metadata.Name;
            }

            samples.Add(previous with
            {
                Title = title,
                Foreground = previous.Hwnd == foreground,
                Bounds = NativeDesktop.Bounds(previous.Hwnd),
                Minimized = NativeDesktop.Minimized(previous.Hwnd),
                Modal = previous.Modal || owner != 0 && !NativeDesktop.Enabled(owner),
                Owner = owner,
                Inaccessible = AccessState(previous.Hwnd, previous.Pid, process),
            });
        }

        // A newly foreground window needs discovery before it can be targeted.
        if (foreground != 0 && !samples.Any(window => window.Hwnd == foreground))
        {
            return false;
        }

        _index.UpdateWindows(samples);
        return true;
    }

    private (string Name, long Start, bool Elevated) ProcessInfo(int pid)
    {
        var now = Stopwatch.GetTimestamp();
        if (_processes.TryGetValue(pid, out var cached))
        {
            if (cached.Identity.IsAlive && Stopwatch.GetElapsedTime(cached.ReadAt, now).TotalSeconds < 5)
            {
                cached.LastUsedAt = now;
                return cached.Identity.Info;
            }

            cached.Identity.Dispose();
            _processes.Remove(pid);
        }

        var identity = NativeDesktop.OpenProcessIdentity(pid);
        if (identity is null)
        {
            // Failed queries are never cached as evidence that a future process with this PID is accessible.
            return NativeDesktop.ReadProcess(pid);
        }

        if (_processes.Count >= ProcessCacheLimit)
        {
            var oldestPid = 0;
            var oldestUse = long.MaxValue;
            foreach (var (candidatePid, candidate) in _processes)
            {
                if (candidate.LastUsedAt < oldestUse)
                {
                    oldestPid = candidatePid;
                    oldestUse = candidate.LastUsedAt;
                }
            }

            _processes[oldestPid].Identity.Dispose();
            _processes.Remove(oldestPid);
        }

        _processes.Add(pid, new CachedProcess(identity, now));
        return identity.Info;
    }

    public void Dispose()
    {
        foreach (var cached in _processes.Values) cached.Identity.Dispose();
        _processes.Clear();
        _windowMetadata.Clear();
    }

    /// <summary>Why a window cannot be used right now: the process-level reason, else "slow" while its provider is being skipped.</summary>
    private string? AccessState(long hwnd, int pid, (string Name, long Start, bool Elevated) process) =>
        Inaccessible(pid, process) ?? (_slowWindows.IsSlow(hwnd, pid, process.Start) ? "slow" : null);

    /// <summary>UI Automation gave up waiting for the provider (its own transaction or connection time limit).</summary>
    internal static bool UiaTimedOut(COMException ex) =>
        ex.HResult is unchecked((int)0x80131505) or unchecked((int)0x8001011F);

    private string? Inaccessible(int pid, (string Name, long Start, bool Elevated) process)
    {
        if (!_onInputDesktop || DeletePolicy.DefaultBlockedProcesses.Contains(process.Name))
        {
            return "secure_desktop";
        }

        if (pid == Environment.ProcessId || _blockedProcesses.Contains(process.Name))
        {
            return "blocked_by_policy";
        }

        return process.Elevated ? "elevated" : null;
    }

    public Observation Observe(DesktopObserveRequest request, CancellationToken ct, bool fresh = false)
    {
        if (request.Scope is not ("window" or "focused" or "dialog"))
        {
            throw new DesktopFault(ActStatus.Blocked, ReasonCodes.UnsupportedAction, "Scope must be window, focused, or dialog.");
        }

        RefreshWindows(ct);
        var window = RequireWindow(request.Window);
        if (request.Scope == "dialog")
        {
            window = TopDialog(window) ?? throw new DesktopFault(ActStatus.Blocked, ReasonCodes.NotFound, "No modal dialog was found.");
        }

        var start = Stopwatch.GetTimestamp();
        var snapshot = Snapshot(window, fresh, ct);
        return DesktopObservation.Render(snapshot, request.SinceGeneration is { } since ? window.Snapshot(since) : null,
            request, Timing.Total(Stopwatch.GetElapsedTime(start).TotalMilliseconds));
    }

    private DesktopWindow RequireWindow(string? selector) => _index.ResolveWindow(selector) ??
        throw new DesktopFault(ActStatus.Blocked, ReasonCodes.NotFound, "No unique window matched. Use a window ID from windows_list.");

    private DesktopWindow? TopDialog(DesktopWindow window)
    {
        for (var depth = 0; depth < 32 && window.BlockedBy is { } id && _index.ById(id) is { } dialog; depth++)
        {
            window = dialog;
        }

        return window.Sample.Modal ? window : null;
    }

    private DesktopSnapshot Snapshot(DesktopWindow window, bool fresh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stamp = _events.Version;
        if (!_reads.TryGetValue(window.Id, out var read))
        {
            read = new WindowRead();
            _reads.Add(window.Id, read);
        }

        var accessible = _onInputDesktop && window.Sample.Inaccessible is null && !window.Sample.Minimized;
        if (!fresh && accessible && window.Latest is { } cached && read.EventVersion == stamp &&
            cached.Window == window.Info && cached.Bounds == window.Sample.Bounds && cached.BlockedBy == window.BlockedBy &&
            Stopwatch.GetElapsedTime(read.ReadAt).TotalMilliseconds < (_events.Available ? 500 : 25))
        {
            return cached;
        }

        var tree = new TreeRead(window.Sample.Hwnd);
        if (accessible)
        {
            var root = _uia.ElementFromHandleBuildCache((nint)window.Sample.Hwnd, _elementCache);
            var clip = NativeDesktop.Intersect(window.Sample.Bounds, NativeDesktop.VirtualScreen);
            ReadTree(root, null, 0, 0, clip, null, tree, ct);
            var focused = window.Sample.Foreground ? _uia.GetFocusedElement() : null;
            var focusedIdentity = focused is null ? null : Identity(_walker.NormalizeElement(focused));
            for (var i = 0; i < tree.Samples.Count; i++)
            {
                var sample = tree.Samples[i];
                tree.Samples[i] = sample with { Focused = sample.Identity == focusedIdentity };
            }
        }

        var snapshot = _index.UpdateElements(window, tree.Samples, tree.Offscreen, tree.Truncated);
        foreach (var (identity, element) in tree.Live)
        {
            read.Elements[identity] = element;
        }

        foreach (var identity in read.Elements.Keys)
        {
            if (!window.Refs.ContainsKey(identity))
            {
                read.Elements.Remove(identity);
            }
        }

        read.EventVersion = stamp;
        read.ReadAt = Stopwatch.GetTimestamp();
        return snapshot;
    }

    private void ReadTree(IUIAutomationElement element, string? parent, int depth, int index, ScreenRect clip,
        ScrollPosition? scroll, TreeRead tree, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (tree.Samples.Count >= MaxSnapshotElements || tree.Scanned++ >= MaxScannedElements)
        {
            tree.Truncated++;
            return;
        }

        var identity = CachedIdentity(element);
        var native = (long)element.CachedNativeWindowHandle;
        if (identity is null || tree.Live.ContainsKey(identity) ||
            depth > 0 && native != 0 && NativeDesktop.Root(native) != tree.Window)
        {
            return;
        }

        var sample = Sample(element, identity, parent, index, clip, scroll, includeValue: true);
        if (depth > 0 && (element.CachedIsOffscreen != 0 ||
            NativeDesktop.HasArea(sample.Bounds) && !NativeDesktop.OnMonitor(sample.VisibleBounds)))
        {
            tree.Offscreen++;
            return;
        }

        tree.Live.Add(identity, element);
        tree.Samples.Add(sample);
        if (depth >= _config.Observe.MaxDepth)
        {
            if (_walker.GetFirstChildElementBuildCache(element, _elementCache) is not null) tree.Truncated++;
            return;
        }

        // Batch immediate raw children only when every child is already a control. Otherwise use
        // the logical walker, which reaches controls behind non-control Save As intermediates.
        var siblings = element.FindAllBuildCache(TreeScope.TreeScope_Children, _uia.RawViewCondition, _elementCache);
        var batch = siblings.Length <= MaxScannedElements - tree.Scanned;
        for (var i = 0; batch && i < siblings.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (siblings.GetElement(i).CachedIsControlElement == 0) batch = false;
        }
        var child = batch ? (siblings.Length == 0 ? null : siblings.GetElement(0))
            : _walker.GetFirstChildElementBuildCache(element, _elementCache);
        var childClip = NativeDesktop.HasArea(sample.Bounds) ? sample.VisibleBounds : clip;
        var childScroll = ScrollContext(element, identity) ?? scroll;
        var siblingIdentities = new HashSet<string>(StringComparer.Ordinal);
        var siblingBudget = MaxScannedElements - tree.Scanned;
        var childIndex = 0;
        while (child is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (childIndex >= siblingBudget || tree.Samples.Count >= MaxSnapshotElements || tree.Scanned >= MaxScannedElements)
            {
                // Sibling enumeration is intentionally bounded; omitted counts are lower bounds.
                tree.Truncated++;
                break;
            }

            var indexInParent = childIndex++;
            try
            {
                var childIdentity = CachedIdentity(child);
                if (childIdentity is not null && !siblingIdentities.Add(childIdentity))
                {
                    tree.Truncated++;
                    break;
                }

                ReadTree(child, identity, depth + 1, indexInParent, childClip, childScroll, tree, ct);
            }
            catch (COMException ex) when (ElementUnavailable(ex))
            {
                // A provider removed the control during enumeration.
            }

            try
            {
                child = batch ? (childIndex < siblings.Length ? siblings.GetElement(childIndex) : null)
                    : _walker.GetNextSiblingElementBuildCache(child, _elementCache);
            }
            catch (COMException ex) when (ElementUnavailable(ex))
            {
                // A disappearing sibling can invalidate the walker cursor; don't restart unboundedly.
                tree.Truncated++;
                break;
            }
        }
    }

    private static ElementSample Sample(IUIAutomationElement element, string identity, string? parent, int index,
        ScreenRect clip, ScrollPosition? scroll, bool includeValue)
    {
        var bounds = element.CachedBoundingRectangle;
        var rect = NativeDesktop.Rect(bounds.left, bounds.top, bounds.right, bounds.bottom);
        var visible = element.CachedIsOffscreen == 0 ? NativeDesktop.Intersect(rect, clip) : NativeDesktop.Empty;
        var password = element.CachedIsPassword != 0;
        var role = Role(element.CachedControlType);
        var readOnly = IsTrue(element, UIA_ValueIsReadOnlyPropertyId);
        var actions = Capabilities(element, role, readOnly, visible);
        var value = includeValue && !password && (actions.HasFlag(ElementActions.Value) || role is "edit" or "document" ||
            IsTrue(element, UIA_IsValuePatternAvailablePropertyId)) ? ReadValue(element, MaxObservationValue + 1) : null;
        return new ElementSample(identity, parent, element.CachedProcessId, role, element.CachedName ?? string.Empty,
            element.CachedAutomationId ?? string.Empty, element.CachedClassName ?? string.Empty, index, rect, visible,
            element.CachedIsEnabled != 0, password, element.CachedHasKeyboardFocus != 0, readOnly,
            ToggleName(element.GetCachedPropertyValueEx(UIA_ToggleToggleStatePropertyId, 1)),
            ExpandName(element.GetCachedPropertyValueEx(UIA_ExpandCollapseExpandCollapseStatePropertyId, 1)),
            IsTrue(element, UIA_SelectionItemIsSelectedPropertyId), element.CachedIsRequiredForForm != 0,
            element.GetCachedPropertyValueEx(UIA_IsDataValidForFormPropertyId, 1) is false, value, actions, scroll);
    }

    private static ElementActions Capabilities(IUIAutomationElement element, string role, bool readOnly, ScreenRect visible)
    {
        var focusable = element.CachedIsKeyboardFocusable != 0;
        var value = IsTrue(element, UIA_IsValuePatternAvailablePropertyId);
        var actions = ElementActions.None;
        if (IsTrue(element, UIA_IsInvokePatternAvailablePropertyId)) actions |= ElementActions.Invoke;
        if (IsTrue(element, UIA_IsTogglePatternAvailablePropertyId)) actions |= ElementActions.Toggle;
        if (IsTrue(element, UIA_IsSelectionItemPatternAvailablePropertyId)) actions |= ElementActions.Select;
        if (IsTrue(element, UIA_IsExpandCollapsePatternAvailablePropertyId)) actions |= ElementActions.ExpandCollapse;
        if (value && !readOnly) actions |= ElementActions.Value;
        if (focusable) actions |= ElementActions.Focus;
        if (focusable && !readOnly && (role == "edit" || value && role is "combobox" or "custom" ||
            role == "document" && EditableDocument(element))) actions |= ElementActions.Type;
        if (IsTrue(element, UIA_IsScrollItemPatternAvailablePropertyId)) actions |= ElementActions.ScrollIntoView;
        if (IsTrue(element, UIA_IsScrollPatternAvailablePropertyId)) actions |= ElementActions.Scroll;
        if (NativeDesktop.HasArea(visible) && SupportsPhysicalClick(role, actions))
        {
            actions |= ElementActions.Click;
        }

        return actions;
    }

    internal static bool SupportsPhysicalClick(string role, ElementActions actions) =>
        (actions & ~(ElementActions.Focus | ElementActions.ScrollIntoView | ElementActions.Scroll)) != ElementActions.None ||
        role is "button" or "menuitem" or "link" or "tabitem" or "listitem" or "dataitem" or "treeitem" or "checkbox" or
            "radiobutton" or "edit" or "combobox" or "slider" or "spinner" or "scrollbar";

    private static bool EditableDocument(IUIAutomationElement element) =>
        element.GetCurrentPattern(UIA_TextPatternId) is IUIAutomationTextPattern text &&
        text.DocumentRange.GetAttributeValue(UIA_TextAttributeIds.UIA_IsReadOnlyAttributeId) is false;

    private static ScrollPosition? ScrollContext(IUIAutomationElement element, string identity) =>
        element.GetCachedPropertyValueEx(UIA_ScrollHorizontalScrollPercentPropertyId, 1) is double horizontal &&
        element.GetCachedPropertyValueEx(UIA_ScrollVerticalScrollPercentPropertyId, 1) is double vertical
            ? new ScrollPosition(identity, horizontal, vertical) : null;

    private static bool IsTrue(IUIAutomationElement element, int property) =>
        element.GetCachedPropertyValueEx(property, 1) is true;

    private static string? CachedIdentity(IUIAutomationElement element) =>
        element.GetCachedPropertyValueEx(UIA_RuntimeIdPropertyId, 1) is int[] { Length: > 0 } id ? string.Join('.', id) : null;

    private static string? Identity(IUIAutomationElement element) =>
        element.GetRuntimeId() is { Length: > 0 } id ? string.Join('.', id) : null;

    private static string? ReadValue(IUIAutomationElement element, int maxLength)
    {
        if (element.CurrentIsPassword != 0)
        {
            return null;
        }

        return element.GetCurrentPattern(UIA_ValuePatternId) is IUIAutomationValuePattern value
            ? value.CurrentValue
            : (element.GetCurrentPattern(UIA_TextPatternId) as IUIAutomationTextPattern)?.DocumentRange?.GetText(maxLength);
    }

    private static string? ToggleName(object? value) => value switch
    {
        0 => "off",
        1 => "on",
        2 => "mixed",
        _ => null,
    };

    private static string? ExpandName(object? value) => value switch
    {
        0 => "collapsed",
        1 or 2 => "expanded",
        _ => null,
    };

    private static string Role(int type) => type switch
    {
        UIA_ButtonControlTypeId => "button", UIA_CalendarControlTypeId => "calendar", UIA_CheckBoxControlTypeId => "checkbox",
        UIA_ComboBoxControlTypeId => "combobox", UIA_EditControlTypeId => "edit", UIA_HyperlinkControlTypeId => "link",
        UIA_ImageControlTypeId => "image", UIA_ListItemControlTypeId => "listitem", UIA_ListControlTypeId => "list",
        UIA_MenuControlTypeId => "menu", UIA_MenuBarControlTypeId => "menubar", UIA_MenuItemControlTypeId => "menuitem",
        UIA_ProgressBarControlTypeId => "progressbar", UIA_RadioButtonControlTypeId => "radiobutton",
        UIA_ScrollBarControlTypeId => "scrollbar", UIA_SliderControlTypeId => "slider", UIA_SpinnerControlTypeId => "spinner",
        UIA_StatusBarControlTypeId => "statusbar", UIA_TabControlTypeId => "tab", UIA_TabItemControlTypeId => "tabitem",
        UIA_TextControlTypeId => "text", UIA_ToolBarControlTypeId => "toolbar", UIA_ToolTipControlTypeId => "tooltip",
        UIA_TreeControlTypeId => "tree", UIA_TreeItemControlTypeId => "treeitem", UIA_GroupControlTypeId => "group",
        UIA_ThumbControlTypeId => "thumb", UIA_DataGridControlTypeId => "datagrid", UIA_DataItemControlTypeId => "dataitem",
        UIA_DocumentControlTypeId => "document", UIA_SplitButtonControlTypeId => "splitbutton", UIA_WindowControlTypeId => "window",
        UIA_PaneControlTypeId => "pane", UIA_HeaderControlTypeId => "header", UIA_HeaderItemControlTypeId => "headeritem",
        UIA_TableControlTypeId => "table", UIA_TitleBarControlTypeId => "titlebar", UIA_SeparatorControlTypeId => "separator",
        UIA_SemanticZoomControlTypeId => "semanticzoom", UIA_AppBarControlTypeId => "appbar", _ => "custom",
    };

    private static bool ElementUnavailable(COMException ex) => ex.HResult == unchecked((int)0x80040201);

    private sealed class WindowRead
    {
        internal Dictionary<string, IUIAutomationElement> Elements { get; } = new(StringComparer.Ordinal);
        internal long EventVersion { get; set; } = -1;
        internal long ReadAt { get; set; }
    }

    private sealed class CachedProcess(NativeDesktop.ProcessIdentity identity, long readAt)
    {
        internal NativeDesktop.ProcessIdentity Identity { get; } = identity;
        internal long ReadAt { get; } = readAt;
        internal long LastUsedAt { get; set; } = readAt;
    }

    private sealed record WindowMetadata(int Pid, long ProcessStart, long Owner, IUIAutomationElement Element,
        string Name, bool Modal, long EventVersion, long ModalVersion, long ReadAt);

    private sealed class TreeRead(long window)
    {
        internal long Window { get; } = window;
        internal List<ElementSample> Samples { get; } = [];
        internal Dictionary<string, IUIAutomationElement> Live { get; } = new(StringComparer.Ordinal);
        internal int Scanned { get; set; }
        internal int Offscreen { get; set; }
        internal int Truncated { get; set; }
    }
}

public sealed class DesktopFault(ActStatus status, string reason, string detail, string? windowId = null) : Exception(detail)
{
    public ActStatus Status { get; } = status;
    public string Reason { get; } = reason;
    public string? WindowId { get; } = windowId;
}
