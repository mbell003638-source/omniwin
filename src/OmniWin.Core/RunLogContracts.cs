namespace OmniWin.Core;

// ---------- run log (contract §12, WS0). Additive: no existing type changed. ----------

/// <summary>
/// One recorded step: enough to repeat the act later from a fresh selector, and nothing more. Typed, set or clipboard
/// text is never stored: <see cref="ValueParam"/> holds a placeholder name such as "p3" instead of the text.
/// </summary>
/// <param name="StepId">"s1", "s2", … in the order steps were recorded. Returned as <see cref="ActResult.StepId"/>.</param>
/// <param name="Tool">The tool that did the act: desktop_act, desktop_keys, desktop_scroll, browser_act, browser_nav, app_launch, window_manage, audio, media or clipboard_set.</param>
/// <param name="Engine">"desktop", "browser" or "system".</param>
/// <param name="Target">The element acted on (its <see cref="ElementDescriptor.Path"/> is the selector), when the tool targets one.</param>
/// <param name="Args">Literal, non-text arguments (keys, direction, amount, url, window process, volume …). Never typed text.</param>
/// <param name="ValueParam">Placeholder name for the act's text value, or null when the act had none.</param>
/// <param name="ExpectOutcome">The expectation actually met, as rendered in the result, when there was one.</param>
/// <param name="TargetEmptyBefore">Contract 2.6: for a typed act, whether the field was empty before typing (null: not known). Only this
/// yes/no is kept, never the field's text.</param>
public sealed record RunStep(
    string StepId,
    string Tool,
    string Engine,
    string Action,
    ElementDescriptor? Target,
    IReadOnlyDictionary<string, string> Args,
    string? ValueParam,
    string? ExpectOutcome,
    DateTimeOffset At,
    bool? TargetEmptyBefore = null);

/// <summary>A step before the log gives it an id and a time.</summary>
public sealed record RunStepDraft(
    string Tool,
    string Engine,
    string Action,
    ElementDescriptor? Target,
    IReadOnlyDictionary<string, string> Args,
    bool HasValue,
    string? ExpectOutcome,
    bool? TargetEmptyBefore = null);

/// <summary>The session's run log. In memory only; it holds no typed text.</summary>
public interface IRunLog
{
    /// <summary>All retained steps, oldest first.</summary>
    IReadOnlyList<RunStep> Steps { get; }

    /// <summary>Appends a step. A draft with <see cref="RunStepDraft.HasValue"/> gets a fresh placeholder name.</summary>
    RunStep Append(RunStepDraft draft);

    /// <summary>The retained step with this id, or null.</summary>
    RunStep? Get(string stepId);

    /// <summary>Retained steps from <paramref name="fromStepId"/> to <paramref name="toStepId"/>, both included, oldest first. Null when either id is unknown or the range is backwards.</summary>
    IReadOnlyList<RunStep>? Range(string fromStepId, string toStepId);
}
