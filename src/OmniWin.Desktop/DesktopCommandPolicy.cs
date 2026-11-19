using Interop.UIAutomationClient;
using OmniWin.Core;
using OmniWin.Safety;
using System.Text.RegularExpressions;
using static Interop.UIAutomationClient.UIA_PatternIds;

namespace OmniWin.Desktop;

internal sealed partial class UiaDesktop
{
    private static string? CurrentCommandLine(LiveElement live)
    {
        if (live.Sample.Password || live.Element.CurrentIsPassword != 0) return null;
        if (live.Sample.Role is "edit" or "combobox" &&
            OptionalPattern<IUIAutomationValuePattern>(live.Element, UIA_ValuePatternId) is { CurrentIsReadOnly: 0 } editable)
        {
            var value = editable.CurrentValue;
            return value.Length <= TerminalCommandText.MaxCharacters ? value : null;
        }

        if (OptionalPattern<IUIAutomationTextPattern>(live.Element, UIA_TextPatternId) is { } text)
        {
            var selection = text.GetSelection();
            if (selection is { Length: 1 })
            {
                var caret = selection.GetElement(0);
                if (caret.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                    caret, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) != 0) return null;
                var paragraph = caret.Clone();
                // A paragraph covers soft-wrapped input. Providers may promote this to the document.
                paragraph.ExpandToEnclosingUnit(TextUnit.TextUnit_Paragraph);
                var prefix = caret.Clone();
                prefix.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                    TextUnit.TextUnit_Character, -TerminalCommandText.MaxCharacters);
                if (prefix.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                    paragraph, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start) < 0)
                {
                    prefix.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                        paragraph, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                }

                var suffix = caret.Clone();
                suffix.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                    TextUnit.TextUnit_Character, TerminalCommandText.MaxCharacters);
                if (suffix.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                    paragraph, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) > 0)
                {
                    suffix.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                        paragraph, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
                }

                var commandRange = prefix.Clone();
                commandRange.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                    suffix, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
                var value = commandRange.GetText(TerminalCommandText.MaxCharacters + 1);
                var beforeCaret = prefix.GetText(TerminalCommandText.MaxCharacters + 1);
                var checkedSelection = text.GetSelection();
                return checkedSelection is { Length: 1 } && caret.Compare(checkedSelection.GetElement(0)) != 0
                    ? TerminalCommandText.Extract(value, beforeCaret) : null;
            }
        }

        return null;
    }

    private static bool CommandTarget(ElementDescriptor? descriptor, DesktopWindow window) =>
        descriptor?.IsPassword != true &&
        (DeletePolicy.IsCommandTarget(descriptor, descriptor?.Process, window.Sample.Title) ||
         DeletePolicy.IsCommandTarget(descriptor, window.Sample.Process, window.Sample.Title));

    private static bool HasDeleteCommand(DesktopCommand command, LiveElement live)
    {
        return ContainsProjectedDeleteCommand(command, CurrentCommandLine(live), live.Sample.Name);
    }

    // ---- F1: a paste puts text into a command window without the typed-command check ever seeing it ----

    /// <summary>ctrl+v, ctrl+shift+v (Windows Terminal) and shift+insert paste the clipboard.</summary>
    private static bool IsPasteChord(KeyChord chord) =>
        chord.Modifiers.HasFlag(KeyModifiers.Control) && chord.Name == "v" ||
        chord.Modifiers.HasFlag(KeyModifiers.Shift) && chord.Name == "insert";

    /// <summary>
    /// A right-click in a terminal pastes in Windows Terminal and the console, and a click or invoke on a menu entry named
    /// Paste pastes as well. Any right-click on a command target is treated as a paste (conservative): the result only
    /// matters when the clipboard holds a delete command.
    /// </summary>
    private static bool IsPasteAction(DesktopCommand command, string? elementName) =>
        command.Action == "right_click" ||
        command.Action is "click" or "double_click" or "invoke" &&
        elementName is not null && elementName.TrimStart().StartsWith("Paste", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when pasting the clipboard into <paramref name="line"/> would give a delete command. When the clipboard cannot be
    /// read, it is unknown what would be pasted into a command window, so the paste is refused (conservative). A clipboard
    /// with no text (a picture or files) cannot be a command.
    /// </summary>
    private static bool PastesDeleteCommand(string? line)
    {
        var clipboard = PasteClipboard.Reader();
        if (!clipboard.Readable) return true;
        if (string.IsNullOrEmpty(clipboard.Text)) return false;
        return DeletePolicy.IsDeleteCommand(clipboard.Text) || DeletePolicy.IsDeleteCommandLine((line ?? string.Empty) + clipboard.Text);
    }

    internal static bool ContainsProjectedDeleteCommand(DesktopCommand command, string? line, string? elementName = null)
    {
        line = line?.TrimEnd('\r', '\n');
        if (command.Value is null && command.Keys is null && IsPasteAction(command, elementName))
        {
            return PastesDeleteCommand(line);
        }

        if (command.Value is { } value)
        {
            return DeletePolicy.IsDeleteCommand(value) || command.Action == "type" &&
                line is not null && DeletePolicy.IsDeleteCommandLine(line + value);
        }

        if (command.Keys is not { } keys) return false;
        var projected = line ?? string.Empty;
        var typed = false;
        foreach (var chord in keys.Chords)
        {
            if (chord.Name == "enter")
            {
                if (DeletePolicy.IsDeleteCommandLine(projected)) return true;
                projected = string.Empty;
            }
            else if (IsPasteChord(chord))
            {
                var clipboard = PasteClipboard.Reader();
                if (!clipboard.Readable) return true;   // unknown text going into a command window
                if (!string.IsNullOrEmpty(clipboard.Text))
                {
                    if (DeletePolicy.IsDeleteCommand(clipboard.Text)) return true;
                    typed = true;
                    projected += clipboard.Text;
                }
            }
            else if (chord.Modifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                typed |= chord.Name == "space" || chord.Name.Length == 1;
                projected += chord.Name switch
                {
                    "space" => " ",
                    { Length: 1 } => chord.Name,
                    _ => string.Empty,
                };
            }
        }

        return typed && DeletePolicy.IsDeleteCommandLine(projected);
    }

    private void CheckCommandSubmission(InputTarget target, KeyChord chord)
    {
        if (chord.Name != "enter" || target.Live is not { } live ||
            !CommandTarget(Describe(target.Window, live.Sample, live.Ref ?? string.Empty) with
                { Path = live.Path }, target.Window)) return;
        RequireSafeSubmission(target);
    }

    private static bool TypedSubmission(string value) => value.AsSpan().IndexOfAny('\r', '\n') >= 0;

    private void CheckTypedCommandSubmission(InputTarget target, string? value)
    {
        if (value is null || !TypedSubmission(value) || target.Live is not { } live ||
            !CommandTarget(Describe(target.Window, live.Sample, live.Ref ?? string.Empty) with
                { Path = live.Path }, target.Window)) return;
        RequireSafeSubmission(target, value);
    }

    private void RequireSafeSubmission(InputTarget target, string insertion = "")
    {
        // The submission guard exists only to keep delete commands out; safety.allow_delete turns it off (no OCR either).
        if (!EnforcesSubmissionGuard(_config)) return;
        var line = CurrentCommandLine(target.Live!);
        // UI Automation cannot read the command line of a full-screen terminal app (opencode and similar draw their own
        // input box). Then the window's visible text, read by OCR, is checked for a delete command instead.
        var screen = line is null ? ReadSubmissionScreenText(target) : null;
        var failure = SubmissionFailure(line, insertion, screen);
        if (failure == ReasonCodes.UnsupportedAction)
        {
            throw Block(ReasonCodes.UnsupportedAction,
                "The command line cannot be read safely before submission (UI Automation and OCR found no command text).");
        }

        if (failure == ReasonCodes.PolicyForbidden)
        {
            throw new DesktopFault(ActStatus.Denied, ReasonCodes.PolicyForbidden, line is null
                ? "The terminal's visible text (read by OCR) contains a deletion command."
                : "The current command line contains a deletion command.");
        }
    }

    internal static bool EnforcesSubmissionGuard(OmniWinConfig config) => !config.Safety.AllowDelete;

    /// <summary>OCR of the target terminal window, or null (no reader, a password field, or nothing recognised).</summary>
    private string? ReadSubmissionScreenText(InputTarget target)
    {
        if (_screenText is null || target.Live is not { } live ||
            live.Sample.Password || live.Element.CurrentIsPassword != 0) return null;
        using (DesktopDiagnostics.Measure("SubmissionOcr"))
        {
            return _screenText.ReadWindowText(target.Window.Sample.Hwnd);
        }
    }

    /// <summary>
    /// Why Enter (or typed text with a newline) may not be submitted to a command window: <c>policy_forbidden</c> when the
    /// command text holds a delete command, <c>unsupported_action</c> when nothing could be read, null when it is safe.
    /// <paramref name="line"/> is the command line read by UI Automation. Only when it is null, <paramref name="screenText"/>
    /// (the window's text read by OCR) is checked instead, more broadly: every line and framed pane on its own as well as
    /// the whole text, each with and without the insertion. No OCR text keeps the block.
    /// </summary>
    internal static string? SubmissionFailure(string? line, string insertion, string? screenText = null)
    {
        if (line is not null)
        {
            return UnsafeCommandText(line) || UnsafeCommandText(line + insertion) ? ReasonCodes.PolicyForbidden : null;
        }

        if (string.IsNullOrWhiteSpace(screenText)) return ReasonCodes.UnsupportedAction;
        return UnsafeScreenText(screenText, insertion) ? ReasonCodes.PolicyForbidden : null;
    }

    private static bool UnsafeCommandText(string value) => DeletePolicy.IsDeleteCommandLine(value) ||
        DeletePolicy.IsDeleteCommandLine(value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));

    /// <summary>
    /// The OCR text of a whole terminal window: scrollback, panes and the app's own input box. Which line will be submitted
    /// is unknown, so any line that reads as a delete command refuses the submission (conservative). Box-drawing and block
    /// characters (TUI frames and cursors) split the text so a command inside a frame is checked on its own, and common
    /// prompt marks in front of a line are ignored.
    /// </summary>
    internal static bool UnsafeScreenText(string screenText, string insertion)
    {
        var text = SplitScreenFrames(screenText);
        if (UnsafeCommandText(text) || UnsafeCommandText(text + insertion)) return true;
        if (insertion.Length > 0 && UnsafeCommandText(insertion)) return true;

        var last = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var segment = raw.TrimStart(' ', '\t', '>', '\u276F', '\u203A', '\u00BB', '\u279C', '\u2192', '\u2022', '\u00B7', '*').TrimEnd();
            if (segment.Length == 0) continue;
            if (UnsafeCommandText(segment)) return true;
            last = segment;
        }

        return last.Length > 0 && insertion.Length > 0 && UnsafeCommandText(last + insertion);
    }

    private static string SplitScreenFrames(string text)
    {
        var chars = text.Replace("\r\n", "\n", StringComparison.Ordinal).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '\u2500' and <= '\u259F' or '\r') chars[i] = '\n';
        }

        return new string(chars);
    }
}

internal static partial class TerminalCommandText
{
    internal const int MaxCharacters = 65536;

    [GeneratedRegex(@"(?m)^[ \t]*(?:(?:PS[ \t]+)?[a-z]:[^>\r\n]*>[ \t]*|[\w.-]+@[\w.-]+(?::[^\r\n$#]*)?[$#][ \t]*|[$#][ \t]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Prompt();

    internal static string? Extract(string paragraph, string beforeCaret)
    {
        if (paragraph.Length > MaxCharacters || beforeCaret.Length > MaxCharacters ||
            !paragraph.StartsWith(beforeCaret, StringComparison.Ordinal)) return null;
        var prompts = Prompt().Matches(beforeCaret);
        if (prompts.Count == 0) return null;
        var prompt = prompts[^1];
        if (Prompt().Match(paragraph, beforeCaret.Length).Success) return null;
        return paragraph[prompt.Index..].TrimEnd('\r', '\n');
    }
}
