using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using static Interop.UIAutomationClient.TextPatternRangeEndpoint;
using static Interop.UIAutomationClient.UIA_PatternIds;

namespace OmniWin.Desktop;

internal static class DesktopText
{
    // Capture is bounded independently of typing. Larger or unreadable documents can still be typed into,
    // but their default result remains unverified.
    internal const int MaxCapturedCharacters = 262_144;

    // F16 (live run, 2026-11-10): the first characters typed right after an earlier action on the same box came out
    // wrong ("ab" typed at the start of "XYZ" gave "bbXYZ"). It was reproduced only with several characters sent
    // before the control had taken the first one; it is not CJK-specific. The fix that held (30/30 live) is to send
    // one scalar per batch and to wait for each to be taken (the caret moves, or a short settle for the first one)
    // before the next (NativeDesktop.AwaitTypedScalar). The exact Windows-internal mechanism was not proven.
    internal static int UnicodeBatchLimit(string className) => 1;

    // This provider's VK_PACKET path can corrupt a surrogate pair inside a scalar batch. Native
    // caret offsets also do not establish CR/LF normalization. Refuse these before any input.
    internal static bool UnsupportedPacketText(string nativeClass, string text) =>
        nativeClass.Equals("RichEditD2DPT", StringComparison.OrdinalIgnoreCase) &&
        (text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') >= 0 || text.AsSpan().IndexOfAny('\r', '\n') >= 0);

    internal static int UnicodeBatchLength(ReadOnlySpan<char> remaining, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        var length = Math.Min(maximum, remaining.Length);
        if (length > 0 && length < remaining.Length && char.IsHighSurrogate(remaining[length - 1]) &&
            char.IsLowSurrogate(remaining[length]))
        {
            length += length == 1 ? 1 : -1;
        }
        return length;
    }

    internal static string? ExpectedAfterInsertion(string original, int? selectionStart, int? selectionEnd, string insertion)
    {
        if (selectionStart is not { } start || selectionEnd is not { } end ||
            start < 0 || end < start || end > original.Length || original.Length > MaxCapturedCharacters ||
            (long)original.Length - (end - start) + insertion.Length > MaxCapturedCharacters ||
            SplitsSurrogate(original, start) || SplitsSurrogate(original, end))
        {
            return null;
        }

        return string.Concat(original.AsSpan(0, start), insertion.AsSpan(), original.AsSpan(end));
    }

    internal static string? ExpectedFromRanges(string original, ReadOnlySpan<char> prefix, ReadOnlySpan<char> selected,
        ReadOnlySpan<char> suffix, string insertion)
    {
        if ((long)prefix.Length + selected.Length + suffix.Length != original.Length ||
            !original.AsSpan().StartsWith(prefix, StringComparison.Ordinal) ||
            !original.AsSpan(prefix.Length, selected.Length).SequenceEqual(selected) ||
            !original.AsSpan(prefix.Length + selected.Length).SequenceEqual(suffix))
        {
            return null;
        }

        return ExpectedAfterInsertion(original, prefix.Length, prefix.Length + selected.Length, insertion);
    }

    private static bool SplitsSurrogate(string value, int offset) => offset > 0 && offset < value.Length &&
        char.IsHighSurrogate(value[offset - 1]) && char.IsLowSurrogate(value[offset]);
}

internal sealed partial class UiaDesktop
{
    /// <summary>Called after focus is verified, before the first typing batch. Password values are never read.</summary>
    private static string? CaptureTypingExpectation(IUIAutomationElement element, string insertion)
    {
        if (element.CurrentIsPassword != 0) return null;
        var valuePattern = OptionalPattern<IUIAutomationValuePattern>(element, UIA_ValuePatternId);
        var originalValue = valuePattern?.CurrentValue;
        if (originalValue?.Length > DesktopText.MaxCapturedCharacters) return null;

        // Native Edit supplies UTF-16 selection offsets directly. Avoid transferring TextPattern's
        // document and ranges when this bounded read is available.
        var hwnd = (long)element.CurrentNativeWindowHandle;
        if (originalValue is not null && hwnd != 0 &&
            NativeDesktop.TryGetEditSelection(hwnd, out var start, out var end))
        {
            var expected = DesktopText.ExpectedAfterInsertion(originalValue, start, end, insertion);
            if (expected is null || element.CurrentIsPassword != 0 || valuePattern!.CurrentValue != originalValue ||
                !NativeDesktop.TryGetEditSelection(hwnd, out var checkedStart, out var checkedEnd) ||
                checkedStart != start || checkedEnd != end || element.CurrentIsPassword != 0)
            {
                return null;
            }

            return expected;
        }

        var textPattern = OptionalPattern<IUIAutomationTextPattern>(element, UIA_TextPatternId);
        if (textPattern is not null)
        {
            try
            {
                var expected = CaptureTextSelection(element, textPattern, valuePattern, originalValue, insertion);
                if (expected is not null) return expected;
            }
            catch (COMException ex) when (OptionalTextUnavailable(ex))
            {
                // This provider cannot supply a selection that can be verified.
            }
        }

        return null;
    }

    private static string? CaptureTextSelection(IUIAutomationElement element, IUIAutomationTextPattern textPattern,
        IUIAutomationValuePattern? valuePattern, string? originalValue, string insertion)
    {
        var selection = textPattern.GetSelection();
        if (selection is null || selection.Length != 1) return null;
        var selected = selection.GetElement(0);
        var document = textPattern.DocumentRange;
        var original = document.GetText(DesktopText.MaxCapturedCharacters + 1);
        if (original.Length > DesktopText.MaxCapturedCharacters || originalValue is not null && originalValue != original) return null;
        var startComparedToDocumentStart = selected.CompareEndpoints(TextPatternRangeEndpoint_Start, document, TextPatternRangeEndpoint_Start);
        var endComparedToDocumentEnd = selected.CompareEndpoints(TextPatternRangeEndpoint_End, document, TextPatternRangeEndpoint_End);
        var selectionEndpointOrder = selected.CompareEndpoints(TextPatternRangeEndpoint_Start, selected, TextPatternRangeEndpoint_End);
        if (startComparedToDocumentStart < 0 || endComparedToDocumentEnd > 0 || selectionEndpointOrder > 0)
        {
            return null;
        }

        ReadOnlySpan<char> prefixText = [];
        if (startComparedToDocumentStart != 0)
        {
            if (selected.CompareEndpoints(TextPatternRangeEndpoint_Start, document, TextPatternRangeEndpoint_End) == 0)
            {
                prefixText = original.AsSpan();
            }
            else
            {
                var prefix = document.Clone();
                prefix.MoveEndpointByRange(TextPatternRangeEndpoint_End, selected, TextPatternRangeEndpoint_Start);
                prefixText = prefix.GetText(original.Length + 1).AsSpan();
            }
        }

        if (prefixText.Length > original.Length || !original.AsSpan().StartsWith(prefixText, StringComparison.Ordinal)) return null;
        ReadOnlySpan<char> selectedText = [];
        if (endComparedToDocumentEnd == 0)
        {
            selectedText = original.AsSpan(prefixText.Length);
        }
        else if (selectionEndpointOrder != 0)
        {
            selectedText = selected.GetText(original.Length - prefixText.Length + 1).AsSpan();
        }

        if (selectedText.Length > original.Length - prefixText.Length) return null;
        // The range endpoints locate the unchanged suffix. Its content is checked by the final source
        // read, so transferring it separately would duplicate most of a large document.
        var suffixText = original.AsSpan(prefixText.Length + selectedText.Length);
        var expected = DesktopText.ExpectedFromRanges(original, prefixText, selectedText, suffixText, insertion);
        if (expected is null || element.CurrentIsPassword != 0) return null;
        var currentValue = valuePattern is null
            ? textPattern.DocumentRange.GetText(DesktopText.MaxCapturedCharacters + 1)
            : valuePattern.CurrentValue;
        if (element.CurrentIsPassword != 0 ||
            currentValue != (originalValue ?? original))
        {
            return null;
        }

        var checkedSelection = textPattern.GetSelection();
        return checkedSelection is not null && checkedSelection.Length == 1 && selected.Compare(checkedSelection.GetElement(0)) != 0
            ? expected : null;
    }

    private static T? OptionalPattern<T>(IUIAutomationElement element, int pattern) where T : class
    {
        try
        {
            return element.GetCurrentPattern(pattern) as T;
        }
        catch (COMException ex) when (OptionalTextUnavailable(ex))
        {
            return null;
        }
    }

    private static bool OptionalTextUnavailable(COMException error) =>
        error.HResult is unchecked((int)0x80040200) or unchecked((int)0x80040204) or unchecked((int)0x80004001);
}
