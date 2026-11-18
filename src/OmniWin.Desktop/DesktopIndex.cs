using System.Globalization;
using OmniWin.Core;

namespace OmniWin.Desktop;

internal sealed record WindowSample(
    long Hwnd, int Pid, long ProcessStart, string Process, string Title, ScreenRect Bounds,
    bool Foreground = false, bool Minimized = false, bool Modal = false, long Owner = 0, string? Inaccessible = null);

[Flags]
internal enum ElementActions
{
    None = 0,
    Invoke = 1,
    Toggle = 2,
    Select = 4,
    ExpandCollapse = 8,
    Value = 16,
    Type = 32,
    Focus = 64,
    ScrollIntoView = 128,
    Scroll = 256,
    Click = 512,
}

internal sealed record ScrollPosition(string Container, double Horizontal, double Vertical);

internal sealed record ElementSample(
    string Identity, string? Parent, int Pid, string Role, string Name, string AutomationId, string ClassName,
    int Index, ScreenRect Bounds, ScreenRect VisibleBounds, bool Enabled = true, bool Password = false,
    bool Focused = false, bool ReadOnly = false, string? Toggle = null, string? Expanded = null,
    bool Selected = false, bool Required = false, bool Invalid = false, string? Value = null,
    ElementActions Actions = ElementActions.None, ScrollPosition? Scroll = null)
{
    internal bool SameTarget(ElementSample current) =>
        Identity == current.Identity && Parent == current.Parent && Pid == current.Pid && Role == current.Role &&
        Name == current.Name && AutomationId == current.AutomationId && ClassName == current.ClassName &&
        Password == current.Password && Enabled == current.Enabled && ReadOnly == current.ReadOnly;

    internal bool PositionMatches(ElementSample current) =>
        Close(Bounds.X, current.Bounds.X) && Close(Bounds.Y, current.Bounds.Y) &&
        Close(Bounds.Width, current.Bounds.Width) && Close(Bounds.Height, current.Bounds.Height) ||
        Scroll is { } before && current.Scroll is { } after && before.Container == after.Container &&
        before != after && Close(Bounds.Width, current.Bounds.Width) && Close(Bounds.Height, current.Bounds.Height);

    private static bool Close(int a, int b) => Math.Abs((long)a - b) <= 4;
}

internal sealed record DesktopElement(string Ref, ElementSample Sample);

internal sealed record DesktopSnapshot(WindowInfo Window, string? BlockedBy, long Generation, ScreenRect Bounds,
    IReadOnlyList<DesktopElement> Elements, int Offscreen, int Truncated);

/// <summary>Session-local identities and immutable snapshot history. Used only from the UIA worker.</summary>
internal sealed class DesktopIndex
{
    private readonly Dictionary<long, DesktopWindow> _byHandle = new();
    private readonly Dictionary<string, DesktopWindow> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DesktopWindow Window, string Identity)> _refs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WindowSample> _retiredWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WindowSample> _retiredElements = new(StringComparer.Ordinal);
    private readonly Queue<string> _retiredWindowIds = new();
    private readonly Queue<string> _retiredElementRefs = new();
    private long _nextWindow;
    private long _nextElement;

    internal IReadOnlyList<DesktopWindow> Windows { get; private set; } = [];

    internal void UpdateWindows(IReadOnlyList<WindowSample> windows)
    {
        var current = new List<DesktopWindow>(windows.Count);
        var present = new HashSet<long>();
        foreach (var sample in windows)
        {
            if (!present.Add(sample.Hwnd))
            {
                continue;
            }

            if (_byHandle.TryGetValue(sample.Hwnd, out var entry) &&
                (entry.Sample.Pid != sample.Pid || entry.Sample.ProcessStart != sample.ProcessStart))
            {
                Remove(entry);
                entry = null;
            }

            if (entry is null)
            {
                entry = new DesktopWindow(NextId('w', ref _nextWindow), sample);
                _byHandle.Add(sample.Hwnd, entry);
                _byId.Add(entry.Id, entry);
            }

            entry.Sample = sample;
            current.Add(entry);
        }

        foreach (var window in Windows)
        {
            if (!present.Contains(window.Sample.Hwnd))
            {
                Remove(window);
            }
        }

        foreach (var window in current)
        {
            window.OwnerId = _byHandle.GetValueOrDefault(window.Sample.Owner)?.Id;
            window.BlockedBy = null;
        }

        foreach (var window in current)
        {
            if (window.Sample.Modal && !window.Sample.Minimized && _byHandle.TryGetValue(window.Sample.Owner, out var owner) &&
                (owner.BlockedBy is null || window.Sample.Foreground))
            {
                owner.BlockedBy = window.Id;
            }
        }

        Windows = current;
    }

    internal DesktopWindow? ByHandle(long hwnd) => _byHandle.GetValueOrDefault(hwnd);

    internal DesktopWindow? ById(string id) => _byId.GetValueOrDefault(id);

    internal WindowSample? KnownWindow(string id) => ById(id)?.Sample ?? _retiredWindows.GetValueOrDefault(id);

    internal WindowSample? KnownElementWindow(string elementRef) =>
        _refs.TryGetValue(elementRef, out var active) ? active.Window.Sample : _retiredElements.GetValueOrDefault(elementRef);

    internal bool IssuedWindow(string id) => Issued(id, 'w', _nextWindow);

    internal bool IssuedElement(string id) => Issued(id, 'e', _nextElement);

    internal DesktopWindow? ResolveWindow(string? selector)
    {
        if (selector is null)
        {
            return Windows.FirstOrDefault(window => window.Sample.Foreground);
        }

        selector = selector.Trim();
        if (selector.Length == 0)
        {
            return null;
        }

        if (selector.Length > 1 && selector[0] is 'w' or 'W' && selector.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return ById(selector.ToLowerInvariant());
        }

        var process = selector.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? selector : selector + ".exe";
        var byProcess = ResolveMatch(window => window.Sample.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
        return byProcess.HadMatch ? byProcess.Window :
            ResolveMatch(window => window.Sample.Title.Contains(selector, StringComparison.OrdinalIgnoreCase)).Window;
    }

    internal DesktopWindow? FindRef(string elementRef, out string? identity)
    {
        if (_refs.TryGetValue(elementRef, out var entry))
        {
            identity = entry.Identity;
            return entry.Window;
        }

        identity = null;
        return null;
    }

    internal DesktopSnapshot UpdateElements(DesktopWindow window, IReadOnlyList<ElementSample> samples, int offscreen, int truncated)
    {
        var elements = new List<DesktopElement>(samples.Count);
        foreach (var sample in samples)
        {
            if (!window.Refs.TryGetValue(sample.Identity, out var elementRef))
            {
                elementRef = NextId('e', ref _nextElement);
                window.Refs.Add(sample.Identity, elementRef);
                _refs.Add(elementRef, (window, sample.Identity));
            }

            elements.Add(new DesktopElement(elementRef, sample));
        }

        var info = window.Info;
        if (window.Latest is { } latest && latest.Window == info && latest.BlockedBy == window.BlockedBy &&
            latest.Bounds == window.Sample.Bounds && latest.Offscreen == offscreen && latest.Truncated == truncated &&
            latest.Elements.SequenceEqual(elements))
        {
            return latest;
        }

        var snapshot = new DesktopSnapshot(info, window.BlockedBy, (window.Latest?.Generation ?? 0) + 1,
            window.Sample.Bounds, elements, offscreen, truncated);
        window.AddSnapshot(snapshot);
        if (window.Refs.Count > 4096)
        {
            var retained = new HashSet<string>(StringComparer.Ordinal);
            foreach (var history in window.History)
            {
                foreach (var element in history.Elements)
                {
                    retained.Add(element.Sample.Identity);
                }
            }

            foreach (var (identity, elementRef) in window.Refs)
            {
                if (!retained.Contains(identity))
                {
                    window.Refs.Remove(identity);
                    _refs.Remove(elementRef);
                }
            }
        }

        return snapshot;
    }

    private (bool HadMatch, DesktopWindow? Window) ResolveMatch(Predicate<DesktopWindow> matches)
    {
        DesktopWindow? match = null;
        var ambiguous = false;
        foreach (var window in Windows)
        {
            if (matches(window))
            {
                if (window.Sample.Foreground)
                {
                    return (true, window);
                }

                ambiguous |= match is not null;
                match = window;
            }
        }

        return (match is not null, ambiguous ? null : match);
    }

    private void Remove(DesktopWindow window)
    {
        _byHandle.Remove(window.Sample.Hwnd);
        _byId.Remove(window.Id);
        Remember(_retiredWindows, _retiredWindowIds, window.Id, window.Sample, 128);
        foreach (var elementRef in window.Refs.Values)
        {
            _refs.Remove(elementRef);
            Remember(_retiredElements, _retiredElementRefs, elementRef, window.Sample, 4096);
        }
    }

    private static void Remember(Dictionary<string, WindowSample> entries, Queue<string> order, string id,
        WindowSample sample, int limit)
    {
        if (!entries.TryAdd(id, sample))
        {
            return;
        }

        order.Enqueue(id);
        if (order.Count > limit)
        {
            entries.Remove(order.Dequeue());
        }
    }

    private static string NextId(char prefix, ref long next) => prefix + (++next).ToString(CultureInfo.InvariantCulture);

    private static bool Issued(string id, char prefix, long latest) => id.Length > 1 && id[0] == prefix &&
        long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
        number > 0 && number <= latest && id[1] != '0';
}

internal sealed class DesktopWindow(string id, WindowSample sample)
{
    private const int HistoryLimit = 8;
    private readonly Queue<DesktopSnapshot> _history = new(HistoryLimit);

    internal string Id { get; } = id;
    internal WindowSample Sample { get; set; } = sample;
    internal string? OwnerId { get; set; }
    internal string? BlockedBy { get; set; }
    internal WindowInfo Info => new(Id, Sample.Hwnd, Sample.Pid, Sample.Process, Sample.Title, Sample.Foreground,
        Sample.Minimized, Sample.Modal, OwnerId, Sample.Inaccessible);
    internal Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);
    internal DesktopSnapshot? Latest { get; private set; }
    internal IEnumerable<DesktopSnapshot> History => _history;

    internal DesktopSnapshot? Snapshot(long generation) => _history.FirstOrDefault(snapshot => snapshot.Generation == generation);

    internal void AddSnapshot(DesktopSnapshot snapshot)
    {
        Latest = snapshot;
        if (_history.Count == HistoryLimit)
        {
            _history.Dequeue();
        }

        _history.Enqueue(snapshot);
    }
}
