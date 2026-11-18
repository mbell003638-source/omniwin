using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using OmniWin.Core;
using static Interop.UIAutomationClient.UIA_PatternIds;

namespace OmniWin.Desktop;

/// <summary>Contract 2.6 (review 27, R1): whether a text field is empty right now. A read; the text itself is never returned.</summary>
internal sealed partial class UiaDesktop
{
    public bool? FieldIsEmpty(string elementRef, CancellationToken ct)
    {
        RefreshWindows(ct);
        var window = _index.FindRef(elementRef, out var identity);
        if (window is null || !_onInputDesktop || window.Sample.Inaccessible is not null) return null;
        try
        {
            var element = ResolveElement(window, identity!, null, ct).Element;
            if (element.CurrentIsPassword != 0) return null;          // never read a password field, not even its length
            if (OptionalPattern<IUIAutomationValuePattern>(element, UIA_ValuePatternId) is { } value) return string.IsNullOrEmpty(value.CurrentValue);
            if (OptionalPattern<IUIAutomationTextPattern>(element, UIA_TextPatternId) is { } text) return text.DocumentRange.GetText(1).Length == 0;
            return null;
        }
        catch (DesktopFault)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
