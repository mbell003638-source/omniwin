using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using OmniWin.Core;
using static Interop.UIAutomationClient.UIA_PatternIds;

namespace OmniWin.Desktop;

/// <summary>Contract 2.6: reading the text the user has selected in the control that has the keyboard focus. A read only; no input.</summary>
internal sealed partial class UiaDesktop
{
    /// <summary>The most selected text returned (a longer selection is cut and marked).</summary>
    internal const int MaxSelectionChars = 20000;

    public SelectionRead ReadSelection(int beforeCaretChars, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Inaccessible() reads _onInputDesktop, which only RefreshWindows sets. This read does not list windows, so check the
        // input desktop here; otherwise a fresh server (nothing listed yet) refused every read as "secure desktop".
        _onInputDesktop = NativeDesktop.OnInputDesktop();
        try
        {
            var focused = _uia.GetFocusedElement();
            if (focused is null) return SelectionRead.Failed(ReasonCodes.NotFound, "No control has the keyboard focus.");

            var pid = focused.CurrentProcessId;
            var process = ProcessInfo(pid);
            var hwnd = NativeDesktop.Foreground;
            var title = NativeDesktop.Title(hwnd);
            var name = focused.CurrentName ?? string.Empty;
            var role = Role(focused.CurrentControlType);
            SelectionRead Refuse(string reason, string detail) =>
                new(false, null, null, false, process.Name, title, hwnd, name, role, reason, detail);

            switch (Inaccessible(pid, process))
            {
                case "secure_desktop":
                    return Refuse(ReasonCodes.SecureDesktop, "The secure desktop or a sign-in window has the focus.");
                case "blocked_by_policy":
                    return Refuse(ReasonCodes.PolicyForbidden, $"{process.Name} is a protected program; its text is not read.");
                case "elevated":
                    return Refuse(ReasonCodes.ElevatedTarget, $"{process.Name} runs as administrator; OmniWin cannot read it.");
            }

            if (focused.CurrentIsPassword != 0) return Refuse(ReasonCodes.PolicyForbidden, "The focused control is a password field; it is never read.");

            if (OptionalPattern<IUIAutomationTextPattern>(focused, UIA_TextPatternId) is not { } text)
            {
                return new SelectionRead(false, null, null, false, process.Name, title, hwnd, name, role, null, null);
            }

            var ranges = text.GetSelection();
            if (ranges is null || ranges.Length == 0)
            {
                return new SelectionRead(true, string.Empty, null, false, process.Name, title, hwnd, name, role, null, null);
            }

            var range = ranges.GetElement(0);
            var selected = range.GetText(MaxSelectionChars + 1) ?? string.Empty;
            var cut = selected.Length > MaxSelectionChars;
            if (cut) selected = selected[..MaxSelectionChars];

            string? before = null;
            if (beforeCaretChars > 0)
            {
                // The text that ends at the caret (or at the end of the selection): what a paste has just put there.
                var tail = range.Clone();
                tail.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
                tail.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -beforeCaretChars);
                before = tail.GetText(beforeCaretChars + 1);
            }

            return new SelectionRead(true, selected, before, cut, process.Name, title, hwnd, name, role, null, null);
        }
        catch (COMException)
        {
            return SelectionRead.Failed(ReasonCodes.NotFound, "The focused control went away while it was read.");
        }
    }
}
