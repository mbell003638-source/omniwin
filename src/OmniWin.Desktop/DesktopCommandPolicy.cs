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

    private static void RequireSafeSubmission(InputTarget target, string insertion = "")
    {
        var line = CurrentCommandLine(target.Live!);
        var failure = SubmissionFailure(line, insertion);
        if (failure == ReasonCodes.UnsupportedAction)
        {
            throw Block(ReasonCodes.UnsupportedAction, "The command line cannot be read safely before submission.");
        }

        if (failure == ReasonCodes.PolicyForbidden)
        {
            throw new DesktopFault(ActStatus.Denied, ReasonCodes.PolicyForbidden,
                "The current command line contains a deletion command.");
        }
    }

    internal static string? SubmissionFailure(string? line, string insertion) => line is null
        ? ReasonCodes.UnsupportedAction
        : UnsafeCommandText(line) || UnsafeCommandText(line + insertion) ? ReasonCodes.PolicyForbidden : null;

    private static bool UnsafeCommandText(string value) => DeletePolicy.IsDeleteCommandLine(value) ||
        DeletePolicy.IsDeleteCommandLine(value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));
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
