using OmniWin.Core;

namespace OmniWin.Desktop;

/// <summary>
/// Everything <see cref="DesktopEngine"/> asks of the UI Automation side. <see cref="UiaDesktop"/> is the real one (one instance,
/// used only on the single MTA worker); tests give the engine a fake so its own rules (budgets, phases, lanes, the readback
/// that follows a re-sent character, what a stopped type reports) run without a window and without sending any input.
/// </summary>
internal interface IDesktopBackend
{
    IReadOnlyList<WindowInfo> ListWindows(CancellationToken ct);
    Observation Observe(DesktopObserveRequest request, CancellationToken ct, bool fresh = false);
    ConditionCheck Evaluate(Expectation condition, string? windowId, IReadOnlySet<string>? openBefore, CancellationToken ct);
    ElementDescriptor? DescribeRef(string elementRef, CancellationToken ct);
    ResolvedRef? ResolveSelector(string process, string titlePattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct);
    string? WindowIdOfRef(string? reference);

    PreparedInput Prepare(DesktopCommand command, PreparedInput? previous, CancellationToken ct,
        InputProgress? progress = null, Func<string?>? stopReason = null);
    void Dispatch(DesktopCommand command, PreparedInput prepared, InputProgress progress, Func<string?> stopReason, CancellationToken ct);
    void ContinueTyping(InputProgress progress, CancellationToken ct);
    void CancelTyping(InputProgress progress);
    ConditionCheck? EvaluateDefault(DesktopCommand command, PreparedInput prepared, InputProgress progress, CancellationToken ct);
    Observation? ReadAfter(string windowId, DesktopSnapshot? baseline, long? since, CancellationToken ct);

    /// <summary>The refs and screen rectangles of the window's latest observation, or null when it was never observed.</summary>
    LabelSet? Labels(string windowId);

    /// <summary>Contract 2.6: the focused control's selected text. A backend without text access says "not supported".</summary>
    SelectionRead ReadSelection(int beforeCaretChars, CancellationToken ct) =>
        new(false, null, null, false, string.Empty, string.Empty, 0, string.Empty, string.Empty, null, null);

    /// <summary>Contract 2.6: whether the field behind a ref is empty (null: unknown). Never returns the text.</summary>
    bool? FieldIsEmpty(string elementRef, CancellationToken ct) => null;

    bool BringToForeground(long hwnd, Func<string?> stopReason, CancellationToken ct);

    /// <summary>What the last <see cref="BringToForeground"/> call sent: any window input, and whether the F24 key was tapped.</summary>
    (bool InputSent, bool KeyTapped) LastForegroundAttempt { get; }

    void Dispose();
}
