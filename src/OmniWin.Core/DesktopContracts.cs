namespace OmniWin.Core;

// ---------- desktop engine (contract §7). WS1 implements in OmniWin.Desktop. ----------
// Every act runs: resolve → precheck → gate → [precheck again if the user was asked to confirm] → input → verify.

public interface IDesktopEngine
{
    Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct);
    Task<Observation> ObserveAsync(DesktopObserveRequest req, CancellationToken ct);
    Task<ActResult> ActAsync(DesktopActRequest req, CancellationToken ct);
    Task<ActResult> SendKeysAsync(KeysRequest req, CancellationToken ct);
    Task<ActResult> ScrollAsync(ScrollRequest req, CancellationToken ct);
    Task<WaitResult> WaitForAsync(Expectation condition, int timeoutMs, CancellationToken ct);
    Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct);

    /// <summary>Returns a fresh ref and the generation of the scan that found it (workflows), or null when nothing matches.</summary>
    Task<ResolvedRef?> ResolveSelectorAsync(string process, string windowTitlePattern,
                                       IReadOnlyList<SelectorSegment> path, CancellationToken ct);
}

/// <summary>WS1 implements; WS3 consumes for <c>window_manage focus</c>.</summary>
public interface IForegroundService
{
    /// <summary>True only if the window was verified to be in the foreground afterwards.</summary>
    Task<bool> BringToForegroundAsync(long hwnd, CancellationToken ct);
}

/// <summary>
/// What one focus attempt did: whether the window was verified in front, whether anything was sent, and whether the F24 key was tapped.
/// When a boundary check stopped the attempt (or something else refused it before it started), <c>StopStatus</c>, <c>StopReason</c>
/// and <c>StopDetail</c> say why, so the caller can report the real reason (for example blocked user_active) instead of a generic failure.
/// </summary>
public readonly record struct ForegroundAttempt(bool Verified, bool InputSent, bool KeyTapped,
    ActStatus? StopStatus = null, string? StopReason = null, string? StopDetail = null);

/// <summary>Optional: a foreground service that also says what it sent, so <c>window_manage focus</c> can report the real channel.</summary>
public interface IForegroundDetails
{
    Task<ForegroundAttempt> BringToForegroundDetailedAsync(long hwnd, CancellationToken ct);
}

/// <summary>
/// The one lane for real mouse and keyboard input. A bridged engine call holds it while it works, so OmniWin's own acts (and a
/// second engine call) get an immediate blocked result instead of fighting over the pointer. Null means it is busy right now.
/// </summary>
public interface IInputLane
{
    Task<IDisposable?> TryEnterAsync(string label, CancellationToken ct);
}

/// <summary>
/// Contract 2.6: what the user has selected in the control that has the keyboard focus. <see cref="Supported"/> is false when the
/// app does not expose its text selection; <see cref="Reason"/> is set when the control must not be read (a password field, a
/// protected or elevated program, the secure desktop). <see cref="BeforeCaret"/> is the text that ends at the caret, when asked for.
/// </summary>
public sealed record SelectionRead(bool Supported, string? Text, string? BeforeCaret, bool Truncated, string Process, string WindowTitle,
    long Hwnd, string ElementName, string ElementRole, string? Reason, string? Detail)
{
    public static SelectionRead Failed(string reason, string detail) =>
        new(false, null, null, false, string.Empty, string.Empty, 0, string.Empty, string.Empty, reason, detail);
}

/// <summary>Contract 2.6: whether the text field behind a ref is empty right now (null: unknown, or a password field). Never returns the text.</summary>
public interface IFieldProbe
{
    Task<bool?> FieldIsEmptyAsync(string elementRef, CancellationToken ct);
}

/// <summary>Reads the focused control's selected text. A read: it sends no input.</summary>
public interface ISelectionSource
{
    Task<SelectionRead> ReadSelectionAsync(int beforeCaretChars, CancellationToken ct);
}

public sealed record DesktopObserveRequest(
    string? Window = null,        // "w3", process name ("notepad.exe") or title substring; null = foreground window
    string Scope = "window",      // "window" | "focused" (focused element's container) | "dialog" (top modal only)
    int MaxElements = 150,        // hard cap 1000
    long? SinceGeneration = null, // return a diff when possible
    bool IncludeText = false);    // include static text nodes (labels, paragraphs)

/// <summary>Action: click | double_click | right_click | invoke | toggle | select | expand | collapse | set_value | type | clear | focus | scroll_into_view | hover | drag (Value = the drop target ref).</summary>
public sealed record DesktopActRequest(string Ref, long Generation, string Action, string? Value = null, ExpectSpec? Expect = null);

/// <summary>Keys grammar: "ctrl+s", "enter", "alt+f4", "ctrl+shift+t", "f5"; sequences separated by space: "ctrl+a delete".</summary>
public sealed record KeysRequest(string Keys, string? Window = null, ExpectSpec? Expect = null);

/// <summary>Direction: up | down | left | right. Amount = wheel notches.</summary>
public sealed record ScrollRequest(string? Ref, string? Window, string Direction, int Amount);
