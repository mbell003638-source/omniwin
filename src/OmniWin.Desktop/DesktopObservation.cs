using System.Globalization;
using System.Text;
using OmniWin.Core;

namespace OmniWin.Desktop;

internal static class DesktopObservation
{
    internal static Observation Closed(DesktopSnapshot previous)
    {
        var generation = previous.Generation + 1;
        var target = previous.Window.Id;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{ResultFormatter.UntrustedHeader}\ndiff {target} gen {previous.Generation}→{generation}\n- window {target}");
        return new Observation(target, generation, text, 0, 0, true, previous.Generation, Timing.Total(0));
    }

    internal static Observation Render(DesktopSnapshot snapshot, DesktopSnapshot? baseline, DesktopObserveRequest request, Timing timing)
    {
        var view = Project(snapshot, request);
        var text = new StringBuilder(ResultFormatter.UntrustedHeader).Append('\n');
        if (baseline is null)
        {
            AppendHeader(text, snapshot);
            foreach (var row in view.Rows)
            {
                text.Append('\n').Append(' ', row.Depth * 2);
                AppendElement(text, row.Element);
            }
        }
        else
        {
            var previous = Project(baseline, request);
            text.Append(CultureInfo.InvariantCulture, $"diff {snapshot.Window.Id} gen {baseline.Generation}→{snapshot.Generation}");
            if (snapshot.Window != baseline.Window || snapshot.BlockedBy != baseline.BlockedBy)
            {
                text.Append("\n~ ");
                AppendHeader(text, snapshot);
            }

            var before = previous.Rows.ToDictionary(row => row.Element.Ref, StringComparer.Ordinal);
            foreach (var row in view.Rows)
            {
                if (!before.Remove(row.Element.Ref, out var old))
                {
                    text.Append("\n+ ");
                    AppendElement(text, row.Element);
                }
                else if (row.Element.Sample != old.Element.Sample || row.Depth != old.Depth)
                {
                    text.Append("\n~ ");
                    AppendElement(text, row.Element);
                }
            }

            foreach (var row in before.Values)
            {
                text.Append("\n- ").Append(row.Element.Ref);
            }

            if (previous.Focus != view.Focus)
            {
                text.Append("\nfocus: ").Append(previous.Focus ?? "none").Append(" → ").Append(view.Focus ?? "none");
            }
        }

        if (snapshot.Offscreen + view.Omitted > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nomitted: {snapshot.Offscreen} offscreen, {view.Omitted} non-interactive");
        }

        if (view.Truncated > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\ntruncated: {view.Truncated} more (raise max_elements or narrow scope)");
        }

        return new Observation(snapshot.Window.Id, snapshot.Generation, text.ToString(), view.Rows.Count,
            snapshot.Offscreen + view.Omitted, baseline is not null, baseline?.Generation, timing);
    }

    private static View Project(DesktopSnapshot snapshot, DesktopObserveRequest request)
    {
        var byIdentity = snapshot.Elements.ToDictionary(element => element.Sample.Identity, StringComparer.Ordinal);
        var focus = snapshot.Elements.FirstOrDefault(element => element.Sample.Focused);
        if (request.Scope == "focused" && focus is null)
        {
            return new View([], 0, 0, null);
        }

        var scopeRoot = request.Scope == "focused" ? focus?.Sample.Parent ?? focus?.Sample.Identity : null;
        var inside = new HashSet<string>(StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var omitted = 0;
        foreach (var element in snapshot.Elements)
        {
            var sample = element.Sample;
            if (scopeRoot is not null && sample.Identity != scopeRoot &&
                (sample.Parent is null || !inside.Contains(sample.Parent)))
            {
                continue;
            }

            inside.Add(sample.Identity);
            if (sample.Parent is null && !sample.Focused)
            {
                continue;
            }

            if (!KeepTarget(sample, request.IncludeText))
            {
                omitted++;
                continue;
            }

            keep.Add(sample.Identity);
            var parent = sample.Parent;
            while (parent is not null && inside.Contains(parent) && byIdentity.TryGetValue(parent, out var ancestor))
            {
                if (ancestor.Sample.Parent is not null && !string.IsNullOrWhiteSpace(ancestor.Sample.Name) && keep.Add(parent) &&
                    !KeepTarget(ancestor.Sample, request.IncludeText))
                {
                    omitted--;
                }

                parent = ancestor.Sample.Parent;
            }
        }

        var rows = new List<Row>();
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var max = Math.Clamp(request.MaxElements, 1, 1000);
        foreach (var element in snapshot.Elements)
        {
            var depth = element.Sample.Parent is { } parent ? depths.GetValueOrDefault(parent) : 0;
            var kept = keep.Contains(element.Sample.Identity);
            depths[element.Sample.Identity] = depth + (kept ? 1 : 0);
            if (kept && rows.Count < max)
            {
                rows.Add(new Row(element, depth + 1));
            }
        }

        return new View(rows, omitted, snapshot.Truncated + keep.Count - rows.Count, focus?.Ref);
    }

    private static bool KeepTarget(ElementSample sample, bool includeText)
    {
        if (sample.Focused) return true;
        if (sample.Role == "text" && includeText) return !string.IsNullOrWhiteSpace(sample.Name) || sample.Value is { Length: > 0 };
        if (sample.Actions == ElementActions.None) return false;
        return !string.IsNullOrWhiteSpace(sample.Name) ||
            (sample.Actions & ~(ElementActions.Focus | ElementActions.Click | ElementActions.ScrollIntoView)) != ElementActions.None ||
            UiaDesktop.SupportsPhysicalClick(sample.Role, ElementActions.None);
    }

    private static void AppendHeader(StringBuilder text, DesktopSnapshot snapshot)
    {
        var window = snapshot.Window;
        text.Append(window.IsModal ? "dialog " : "window ").Append(window.Id).Append(' ')
            .Append(ResultFormatter.Quote(window.Title, ResultFormatter.MaxNameChars))
            .Append(" app=").Append(ResultFormatter.OneLine(window.Process))
            .Append(CultureInfo.InvariantCulture, $" pid={window.Pid} gen={snapshot.Generation}");
        if (window.IsForeground) text.Append(" foreground");
        if (window.IsMinimized) text.Append(" minimized");
        if (window.IsModal) text.Append(" modal");
        if (window.OwnerId is { } owner) text.Append(" owner=").Append(owner);
        if (window.Inaccessible is { } reason) text.Append(" inaccessible=").Append(reason);
        if (snapshot.BlockedBy is { } dialog) text.Append(" blocked_by=").Append(dialog);
    }

    private static void AppendElement(StringBuilder text, DesktopElement element)
    {
        var s = element.Sample;
        text.Append(element.Ref).Append(' ').Append(s.Role).Append(' ')
            .Append(ResultFormatter.Quote(s.Name, ResultFormatter.MaxNameChars));
        if (s.Password) text.Append(" value=<redacted>");
        else if (s.Value is not null) text.Append(" value=").Append(ResultFormatter.Quote(s.Value, ResultFormatter.MaxValueChars));
        if (s.Focused) text.Append(" focused");
        if (!s.Enabled) text.Append(" disabled");
        if (s.ReadOnly) text.Append(" readonly");
        if (s.Toggle is { } toggle) text.Append(toggle switch { "on" => " checked", "off" => " unchecked", _ => " mixed" });
        if (s.Expanded is { } expanded) text.Append(' ').Append(expanded);
        if (s.Selected) text.Append(" selected");
        if (s.Required) text.Append(" required");
        if (s.Invalid) text.Append(" invalid");
        if (s.Actions != ElementActions.None)
        {
            var actions = new List<string>();
            if (s.Actions.HasFlag(ElementActions.Click)) actions.Add("click");
            if (s.Actions.HasFlag(ElementActions.Invoke)) actions.Add("invoke");
            if (s.Actions.HasFlag(ElementActions.Toggle)) actions.Add("toggle");
            if (s.Actions.HasFlag(ElementActions.Select)) actions.Add("select");
            if (s.Actions.HasFlag(ElementActions.ExpandCollapse)) actions.Add(s.Expanded == "expanded" ? "collapse" : "expand");
            if (s.Actions.HasFlag(ElementActions.Type)) actions.Add("type");
            if (s.Actions.HasFlag(ElementActions.Value)) actions.Add("set_value");
            if (s.Actions.HasFlag(ElementActions.Focus)) actions.Add("focus");
            if (s.Actions.HasFlag(ElementActions.ScrollIntoView)) actions.Add("scroll_into_view");
            if (s.Actions.HasFlag(ElementActions.Scroll)) actions.Add("scroll");
            text.Append(" [").AppendJoin(',', actions).Append(']');
        }
    }

    private sealed record Row(DesktopElement Element, int Depth);
    private sealed record View(IReadOnlyList<Row> Rows, int Omitted, int Truncated, string? Focus);
}
