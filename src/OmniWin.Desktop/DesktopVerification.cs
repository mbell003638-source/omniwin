using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using OmniWin.Core;
using static Interop.UIAutomationClient.UIA_PropertyIds;

namespace OmniWin.Desktop;

internal sealed record ConditionCheck(bool Met, string Outcome, bool Mismatch = false);

internal sealed partial class UiaDesktop
{
    public ConditionCheck Evaluate(Expectation condition, string? windowId, IReadOnlySet<string>? openBefore, CancellationToken ct)
    {
        ValidateExpectation(condition);
        RefreshWindows(ct, fresh: RequiresFreshWindowScan(condition));
        return EvaluateCurrent(condition, windowId, openBefore, ct);
    }

    private ConditionCheck EvaluateCurrent(Expectation condition, string? windowId, IReadOnlySet<string>? openBefore, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_onInputDesktop)
        {
            return CheckCondition("active input desktop", false);
        }

        switch (condition)
        {
            case WindowOpened opened:
                return CheckCondition("window_opened", _index.Windows.Any(window =>
                    openBefore?.Contains(window.Id) != true && Contains(window.Sample.Title, opened.TitleContains) &&
                    (opened.Process is null || ProcessMatches(window.Sample.Process, opened.Process))));
            case WindowClosed closed:
                return CheckCondition("window_closed", (openBefore?.Contains(closed.Window) ?? _index.IssuedWindow(closed.Window)) &&
                    WindowActuallyClosed(_index.KnownWindow(closed.Window)));
            case ElementAppears appears:
                return CheckCondition("element_appears", AnyVisible(appears.Within, windowId,
                    sample => (appears.Role is null || sample.Role.Equals(appears.Role, StringComparison.OrdinalIgnoreCase)) &&
                        Contains(sample.Name, appears.NameContains), ct));
            case TextAppears text:
                return CheckCondition("text_appears", AnyVisible(text.Within, windowId,
                    sample => !sample.Password && (Contains(sample.Name, text.Value) ||
                        sample.Value is { } value && Contains(value, text.Value)), ct));
            case ElementGone gone:
                return CheckCondition("element_gone", ElementIsGone(gone.Ref));
            case ValueEquals value:
                var current = ReadExpectedValue(LiveRef(value.Ref), value.Value);
                return CheckCondition("value_equals", current == value.Value, current is not null && current != value.Value);
            case FocusOn focus:
                var target = LiveRef(focus.Ref);
                return CheckCondition("focus_on", target is not null && target.CurrentHasKeyboardFocus != 0 &&
                    _index.KnownElementWindow(focus.Ref) is { } focusWindow && NativeDesktop.Foreground == focusWindow.Hwnd &&
                    NativeDesktop.Root(NativeDesktop.FocusHandle()) == focusWindow.Hwnd &&
                    _uia.GetFocusedElement() is { } focused && _uia.CompareElements(focused, target) != 0);
            case ToggleIs toggle:
                return CheckCondition("toggle_is", ToggleName(LiveRef(toggle.Ref)?.GetCurrentPropertyValueEx(UIA_ToggleToggleStatePropertyId, 1))
                    == toggle.State);
            case AllOf all:
                return Combine("all_of", all.Items, true, windowId, openBefore, ct);
            case AnyOf any:
                return Combine("any_of", any.Items, false, windowId, openBefore, ct);
            default:
                return CheckCondition("unsupported expectation", false);
        }
    }

    private ConditionCheck Combine(string label, IReadOnlyList<Expectation> items, bool all, string? window,
        IReadOnlySet<string>? openBefore, CancellationToken ct)
    {
        var checks = items.Select(item => EvaluateCurrent(item, window, openBefore, ct)).ToArray();
        var met = checks.Length != 0 && (all ? checks.All(item => item.Met) : checks.Any(item => item.Met));
        var mismatch = !met && checks.Length != 0 && (all ? checks.Any(item => item.Mismatch) : checks.All(item => item.Mismatch));
        return new ConditionCheck(met, $"{label} [{string.Join(", ", checks.Select(item => item.Outcome))}] → {(met ? "met" : "not met")}", mismatch);
    }

    private static ConditionCheck CheckCondition(string label, bool met, bool mismatch = false) =>
        new(met, $"{label} → {(met ? "met" : "not met")}", mismatch);

    private bool AnyVisible(string? within, string? fallbackWindow, Predicate<ElementSample> matches, CancellationToken ct)
    {
        string? root = null;
        DesktopWindow? window;
        if (within?.StartsWith('e') == true)
        {
            window = _index.FindRef(within, out root);
        }
        else
        {
            window = _index.ResolveWindow(within ?? fallbackWindow);
        }

        if (window is null)
        {
            return false;
        }

        var snapshot = Snapshot(window, true, ct);
        var subtree = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in snapshot.Elements)
        {
            var sample = element.Sample;
            if (root is null || sample.Identity == root || sample.Parent is not null && subtree.Contains(sample.Parent))
            {
                subtree.Add(sample.Identity);
                if (matches(sample)) return true;
            }
        }

        return false;
    }

    private bool ElementIsGone(string reference)
    {
        if (!_index.IssuedElement(reference)) return false;
        if (WindowActuallyClosed(_index.KnownElementWindow(reference))) return true;
        var window = _index.FindRef(reference, out var identity);
        if (window is null) return false;
        if (!_onInputDesktop || window.Sample.Inaccessible is not null) return false;
        if (!_reads.TryGetValue(window.Id, out var read) || !read.Elements.TryGetValue(identity!, out var element)) return false;
        try
        {
            return Identity(element) != identity;
        }
        catch (COMException ex) when (ReferenceUnavailable(ex))
        {
            return true;
        }
    }

    public ConditionCheck? EvaluateDefault(DesktopCommand command, PreparedInput prepared, InputProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Ref is null || command.Ref.StartsWith('o')) return null;
        var window = _index.ById(prepared.Window);
        var element = LiveRef(command.Ref);
        if (element is null || window is null) return null;
        try
        {
            switch (command.Action)
            {
                case "type":
                    if (progress.ExpectedTypeValue is not { } expectedInsertion) return null;
                    var inserted = ReadExpectedValue(element, expectedInsertion);
                    return inserted is null ? null : CheckCondition("text insertion readback",
                        inserted == expectedInsertion, inserted != expectedInsertion);
                case "set_value":
                case "clear":
                    var expected = command.Action == "clear" ? string.Empty : command.Value!;
                    var current = ReadExpectedValue(element, expected);
                    return current is null ? null : CheckCondition("value readback", current == expected, current != expected);
                case "toggle":
                    var toggle = ToggleName(element.GetCurrentPropertyValueEx(UIA_ToggleToggleStatePropertyId, 1));
                    return toggle is null || progress.PreviousToggle is null ? null :
                        CheckCondition("toggle changed", toggle != progress.PreviousToggle, toggle == progress.PreviousToggle);
                case "select":
                    var selected = element.GetCurrentPropertyValueEx(UIA_SelectionItemIsSelectedPropertyId, 1);
                    return selected is bool value ? CheckCondition("selected", value, !value) : null;
                case "expand":
                case "collapse":
                    var expanded = element.GetCurrentPropertyValueEx(UIA_ExpandCollapseExpandCollapseStatePropertyId, 1);
                    var met = expanded is int state && state == (command.Action == "expand" ? 1 : 0);
                    return expanded is int ? CheckCondition(command.Action, met, !met) : null;
                case "focus":
                    var hasFocus = NativeDesktop.Foreground == window.Sample.Hwnd && _uia.GetFocusedElement() is { } focused &&
                        _uia.CompareElements(focused, element) != 0;
                    return CheckCondition("keyboard focus", hasFocus, !hasFocus);
                default:
                    return null;
            }
        }
        catch (COMException ex) when (ReferenceUnavailable(ex))
        {
            return null;
        }
    }

    public Observation? ReadAfter(string windowId, DesktopSnapshot? baseline, long? since, CancellationToken ct)
    {
        RefreshWindows(ct);
        if (_index.ById(windowId) is not { } window)
        {
            return baseline is not null && _onInputDesktop ? DesktopObservation.Closed(baseline) : null;
        }

        var start = Stopwatch.GetTimestamp();
        var current = Snapshot(window, true, ct);
        baseline ??= since is { } generation ? window.Snapshot(generation) : null;
        return DesktopObservation.Render(current, baseline,
            new DesktopObserveRequest(MaxElements: _config.Observe.DefaultMaxElements),
            Timing.Total(Stopwatch.GetElapsedTime(start).TotalMilliseconds));
    }

    public ElementDescriptor? DescribeRef(string elementRef, CancellationToken ct)
    {
        RefreshWindows(ct);
        var window = _index.FindRef(elementRef, out var identity);
        if (window is null || !_onInputDesktop || window.Sample.Inaccessible is not null) return null;
        try
        {
            var live = ResolveElement(window, identity!, null, ct);
            return Describe(window, live.Sample, elementRef);
        }
        catch (DesktopFault)
        {
            return null;
        }
    }

    private ElementDescriptor Describe(DesktopWindow window, ElementSample sample, string elementRef)
    {
        var path = new List<SelectorSegment>();
        var ancestor = sample;
        while (ancestor.Parent is { } parent)
        {
            path.Add(new SelectorSegment(ancestor.Role, NonEmpty(ancestor.AutomationId), NonEmpty(ancestor.Name),
                NonEmpty(ancestor.ClassName), ancestor.Index));
            if (window.Latest?.Elements.FirstOrDefault(node => node.Sample.Identity == parent) is not { } node) break;
            ancestor = node.Sample;
        }

        path.Reverse();
        return new ElementDescriptor(elementRef, "desktop", ProcessInfo(sample.Pid).Name, window.Sample.Title, null, sample.Role,
            sample.Name, NonEmpty(sample.AutomationId), sample.Password, sample.Enabled, path);
    }

    /// <summary>The refs and visible rectangles of a window's latest observation (password fields are never listed).</summary>
    public LabelSet? Labels(string windowId)
    {
        if (_index.ById(windowId)?.Latest is not { } snapshot) return null;
        var labels = snapshot.Elements
            .Where(e => !e.Sample.Password && NativeDesktop.HasArea(e.Sample.VisibleBounds))
            .Select(e => new ElementLabel(e.Ref, e.Sample.VisibleBounds))
            .ToList();
        return new LabelSet(snapshot.Generation, labels);
    }

    /// <summary>The id of the window an element ref belongs to, or null when the ref is unknown. Used to pick the window lock.</summary>
    public string? WindowIdOfRef(string? reference) =>
        string.IsNullOrEmpty(reference) ? null : _index.FindRef(reference, out _)?.Id;

    public ResolvedRef? ResolveSelector(string process, string titlePattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct)
    {
        RefreshWindows(ct);
        // A modern app (Settings, Calculator, Clock) runs inside a frame window owned by ApplicationFrameHost.exe, while its controls
        // report the app's own process (SystemSettings.exe, CalculatorApp.exe). Accept that frame when its title matches the saved one.
        var windows = _index.Windows.Where(window => window.Sample.Inaccessible is null &&
            (ProcessMatches(window.Sample.Process, process) ||
             (titlePattern.Length > 0 && ProcessMatches(window.Sample.Process, "ApplicationFrameHost.exe"))) &&
            (titlePattern.Length == 0 || FileSystemName.MatchesSimpleExpression(titlePattern, window.Sample.Title, ignoreCase: true))).ToArray();
        var target = windows.Length == 1 ? windows[0] : windows.FirstOrDefault(window => window.Sample.Foreground);
        if (target is null || path.Count == 0) return null;

        var snapshot = Snapshot(target, true, ct);
        var elements = snapshot.Elements;
        var current = elements.FirstOrDefault(element => element.Sample.Parent is null);
        foreach (var segment in path)
        {
            if (current is null) break;
            var matches = elements.Where(element => element.Sample.Parent == current.Sample.Identity && Matches(element.Sample, segment)).ToArray();
            current = matches.Length == 1 ? matches[0] : matches.FirstOrDefault(element => element.Sample.Index == segment.Index);
        }

        // Modern apps (Settings, Calculator, anything hosted by ApplicationFrameHost) sometimes add or drop a wrapper level at the top
        // of the window, so the saved path no longer lines up although the control is there. Fall back to the saved control itself:
        // accepted only when exactly one control in this window has the same role, name, AutomationId and class. None or several
        // stops the step as before; it never guesses between lookalikes.
        if (current is null && path.Count > 0 && path[^1] is { } last && (last.Name is { Length: > 0 } || last.AutomationId is { Length: > 0 }))
        {
            var unique = elements.Where(element => Matches(element.Sample, last)).Take(2).ToArray();
            current = unique.Length == 1 ? unique[0] : null;
        }

        return current is null ? null : new ResolvedRef(current.Ref, snapshot.Generation);
    }

    private static bool Matches(ElementSample sample, SelectorSegment segment) =>
        sample.Role == segment.Role && (segment.AutomationId is null || sample.AutomationId == segment.AutomationId) &&
        (segment.Name is null || sample.Name == segment.Name) && (segment.ClassName is null || sample.ClassName == segment.ClassName);

    private IUIAutomationElement? LiveRef(string reference)
    {
        var window = _index.FindRef(reference, out var identity);
        if (window is null || !_onInputDesktop || window.Sample.Inaccessible is not null ||
            WindowActuallyClosed(window.Sample) ||
            !_reads.TryGetValue(window.Id, out var read) || !read.Elements.TryGetValue(identity!, out var element))
        {
            return null;
        }

        try
        {
            var live = ReadLive(window, element, 0, false);
            return live is not null && window.Latest?.Elements.FirstOrDefault(node => node.Ref == reference) is { } observed &&
                SameVerificationTarget(observed.Sample, live.Sample) ? element : null;
        }
        catch (COMException ex) when (ReferenceUnavailable(ex))
        {
            return null;
        }
    }

    private static string? ReadExpectedValue(IUIAutomationElement? element, string expected) =>
        element is null ? null : ReadValue(element, expected.Length == int.MaxValue ? int.MaxValue : expected.Length + 1);

    private static void ValidateExpectation(Expectation condition)
    {
        switch (condition)
        {
            case AllOf all:
                if (all.Items is null || all.Items.Count == 0) break;
                foreach (var item in all.Items) ValidateExpectation(item);
                return;
            case AnyOf any:
                if (any.Items is null || any.Items.Count == 0) break;
                foreach (var item in any.Items) ValidateExpectation(item);
                return;
            case WindowOpened or WindowClosed or ElementAppears or ElementGone or ValueEquals or FocusOn or ToggleIs or TextAppears:
                return;
        }

        throw Block(ReasonCodes.UnsupportedAction, "This expectation is not supported by the desktop engine.");
    }

    private static bool RequiresFreshWindowScan(Expectation condition) => condition switch
    {
        WindowOpened or WindowClosed => true,
        AllOf all => all.Items.Any(RequiresFreshWindowScan),
        AnyOf any => any.Items.Any(RequiresFreshWindowScan),
        _ => false,
    };

    private static bool SameVerificationTarget(ElementSample observed, ElementSample current) =>
        observed.Identity == current.Identity && observed.Parent == current.Parent && observed.Pid == current.Pid &&
        observed.Role == current.Role && observed.Name == current.Name && observed.AutomationId == current.AutomationId &&
        observed.ClassName == current.ClassName && observed.Password == current.Password;

    private static bool WindowActuallyClosed(WindowSample? sample) => sample is not null &&
        (!NativeDesktop.IsWindow(sample.Hwnd) || NativeDesktop.ProcessId(sample.Hwnd) != sample.Pid);

    private static bool Contains(string source, string? needle) => needle is null || source.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool ProcessMatches(string actual, string expected) =>
        string.Equals(actual, expected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? expected : expected + ".exe",
            StringComparison.OrdinalIgnoreCase);

    private static string? NonEmpty(string value) => value.Length == 0 ? null : value;
}
