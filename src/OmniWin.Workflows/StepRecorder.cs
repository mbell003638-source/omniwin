using OmniWin.Core;

namespace OmniWin.Workflows;

/// <summary>What was learned about a call before it ran: the element it targets and the process of the window it addresses.</summary>
public sealed record StepContext(ElementDescriptor? Target, string? WindowProcess, bool? TargetEmptyBefore = null);

/// <summary>
/// Decides which acts are recorded and turns a call into a run-log step. An act is recorded when it ended
/// <c>confirmed</c>, or <c>unverified</c> with a non-empty diff (contract §12). Typed, set and clipboard text is never
/// stored: the step gets a placeholder name instead.
/// </summary>
public sealed class StepRecorder
{
    private readonly IRunLog _log;
    private readonly IDesktopEngine? _desktop;
    private readonly IBrowserEngine? _browser;

    public StepRecorder(IRunLog log, IDesktopEngine? desktop, IBrowserEngine? browser)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _desktop = desktop;
        _browser = browser;
    }

    public IRunLog Log => _log;

    /// <summary>True when an act with this outcome belongs in the run log (and counts as a passed step when it runs again).</summary>
    public static bool IsRecordable(ActStatus status, bool hasDiff) =>
        status == ActStatus.Confirmed || (status == ActStatus.Unverified && hasDiff);

    /// <summary>True when an observation carries at least one added, removed or changed line.</summary>
    public static bool HasDiffLines(Observation? after) => after is not null && HasDiffLines(after.Text);

    /// <summary>
    /// True when result text has a diff line ("+ …", "- …", "~ …") after its first line. Lines in the untrusted header and
    /// the status line never start that way.
    /// </summary>
    public static bool HasDiffLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("+ ", StringComparison.Ordinal) ||
                line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("~ ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Looks up the target element and the window's process before the act, so the step can be replayed from a fresh selector.</summary>
    public async Task<StepContext> PrepareAsync(StepCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ElementDescriptor? target = null;
        try
        {
            if (StepTools.MayHaveRef(call.Tool) && !string.IsNullOrEmpty(call.Ref) && !call.Ref.StartsWith('o'))
            {
                target = StepTools.EngineOf(call.Tool) == StepTools.Browser
                    ? _browser is null ? null : await _browser.DescribeAsync(call.Ref, ct).ConfigureAwait(false)
                    : _desktop is null ? null : await _desktop.DescribeAsync(call.Ref, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            target = null;   // nothing to replay from; the act is simply not recorded
        }

        // Contract 2.6 (R1): for typing into a field, remember only whether it was empty, so a replay never types into text that the
        // first run did not have in front of it.
        bool? emptyBefore = null;
        if (target is not null && call.Tool == "desktop_act" && call.Action == "type" && _desktop is IFieldProbe probe)
        {
            try
            {
                emptyBefore = await probe.FieldIsEmptyAsync(call.Ref!, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                emptyBefore = null;
            }
        }

        return new StepContext(target, await WindowProcessAsync(call.Window, ct).ConfigureAwait(false), emptyBefore);
    }

    /// <summary>"w3" becomes that window's process name; any other text (a process name or title part) is kept as given.</summary>
    public async Task<string?> WindowProcessAsync(string? window, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(window)) return null;
        if (!StepTools.IsWindowId(window)) return window;
        if (_desktop is null) return null;
        try
        {
            var windows = await _desktop.ListWindowsAsync(ct).ConfigureAwait(false);
            return windows.FirstOrDefault(w => string.Equals(w.Id, window, StringComparison.Ordinal))?.Process;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the act when it qualifies and returns the step id, or null when it does not (not recordable, or it targets an
    /// element that could not be described and so cannot be found again).
    /// </summary>
    public string? Record(StepCall call, StepContext context, ActStatus status, bool hasDiff, string? expectOutcome)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        if (!IsRecordable(status, hasDiff) || !StepTools.IsKnown(call.Tool)) return null;

        // An element-targeted act can only be repeated from the element's selector.
        if (StepTools.IsRefBased(call.Tool) && context.Target is null) return null;
        if (call.Tool == "desktop_scroll" && !string.IsNullOrEmpty(call.Ref) && !call.Ref.StartsWith('o') && context.Target is null) return null;

        var (action, args, hasValue) = StepTools.Describe(call, context.WindowProcess);
        var step = _log.Append(new RunStepDraft(call.Tool, StepTools.EngineOf(call.Tool)!, action, context.Target, args, hasValue, expectOutcome, context.TargetEmptyBefore));
        return step.StepId;
    }

    /// <summary>The expectation line of a result text ("expect: …"), without the prefix, or null.</summary>
    public static string? ExpectLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("expect: ", StringComparison.Ordinal)) return line["expect: ".Length..];
        }

        return null;
    }

    /// <summary>
    /// Adds <c>step=sN</c> to the first (status) line of a result text, where the audit filter looks for it. A first line
    /// that already has a step token is left alone.
    /// </summary>
    public static string WithStepToken(string text, string stepId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        var first = newline < 0 ? text : text[..newline];
        if (first.Split(' ').Any(t => t.StartsWith("step=", StringComparison.Ordinal))) return text;
        var rest = newline < 0 ? string.Empty : text[newline..];
        return first + " step=" + stepId + rest;
    }

    /// <summary>Parses the status word and input_sent from a result text's first line.</summary>
    public static bool TryParseStatusLine(string? text, out ActStatus status, out bool inputSent)
    {
        status = ActStatus.Error;
        inputSent = false;
        if (string.IsNullOrEmpty(text)) return false;
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        var first = newline < 0 ? text : text[..newline];
        string? statusWord = null;
        foreach (var token in first.Split(' '))
        {
            if (token.StartsWith("status=", StringComparison.Ordinal)) statusWord = token["status=".Length..];
            else if (token.StartsWith("input_sent=", StringComparison.Ordinal)) inputSent = token["input_sent=".Length..] == "true";
        }

        if (statusWord is null) return false;
        foreach (var candidate in Enum.GetValues<ActStatus>())
        {
            if (string.Equals(ResultFormatter.StatusName(candidate), statusWord, StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        return false;
    }
}
