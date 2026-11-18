using System.Text.RegularExpressions;
using OmniWin.Core;

namespace OmniWin.Desktop;

/// <summary>
/// D1: some controls accept a UI Automation value and read it back correctly, but the app ignores it when it acts
/// (the File name box of a Save as or Open dialog saves under the default name; an address bar does not navigate).
/// A readback match there proves nothing, so <c>set_value</c> and <c>clear</c> on these controls can never be
/// <c>confirmed</c>: the result is <c>unverified</c> and the detail tells the model to use <c>type</c>.
/// <para>
/// The controls are recognised by their structure, never by the window title or the language of their names: the
/// automation ids and class names of the control and its ancestors in <see cref="ElementDescriptor.Path"/>.
/// The English title and name rules remain only as an extra signal.
/// </para>
/// <para>
/// Decision on the safest rule: an id match is enough on its own (a plain app edit that happens to use the same dialog item id is
/// then reported <c>unverified</c> instead of <c>confirmed</c>, which costs nothing but never claims a success that was not checked).
/// Password fields are never matched.
/// </para>
/// </summary>
internal static partial class ValueSettingPolicy
{
    internal const string IgnoredDetail =
        "The control took the value, but file dialogs and address bars often ignore a value set this way, so the effect is not verified. " +
        "Use type here (select all first), then press Enter, and check the result.";

    // Common file dialogs. The File name combobox is item 1148 in the classic comdlg32 dialog and "FileNameControlHost" in the
    // newer item dialog (the inner edit is item 1001 in both). These ids do not depend on the language.
    private static readonly string[] FileNameIds = ["1148", "FileNameControlHost"];

    // The address band of any file dialog or Explorer window: its inner edit is item 41477, and the band's window class is
    // "Address Band Root" (with the breadcrumb toolbar "Breadcrumb Parent").
    private const string AddressEditId = "41477";
    private static readonly string[] AddressClasses = ["Address Band Root", "Breadcrumb Parent"];

    [GeneratedRegex(@"^\s*(save as|save|open|select\b.*|browse\b.*|choose\b.*|export\b.*|import\b.*|upload\b.*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileDialogTitle();

    /// <summary>True when this action on this target must never be reported as confirmed from a readback alone.</summary>
    internal static bool MustNotConfirm(string action, ElementDescriptor? target) =>
        action is "set_value" or "clear" && IsIgnored(target);

    /// <summary>True when a value set on this element cannot be trusted to have taken effect.</summary>
    internal static bool IsIgnored(ElementDescriptor? target)
    {
        if (target is null || target.IsPassword) return false;
        if (target.Role is not ("edit" or "combobox")) return false;

        return IsFileNameBox(target) || IsAddressBar(target);
    }

    private static bool IsFileNameBox(ElementDescriptor target)
    {
        if (HasFileNameId(target.AutomationId)) return true;
        foreach (var segment in target.Path)
        {
            if (HasFileNameId(segment.AutomationId)) return true;
        }

        // Extra signal (English only): an edit or combobox named "File name..." in a window titled like a file dialog.
        return target.Name.StartsWith("File name", StringComparison.OrdinalIgnoreCase) &&
               FileDialogTitle().IsMatch(target.WindowTitle ?? string.Empty);
    }

    private static bool IsAddressBar(ElementDescriptor target)
    {
        if (target.AutomationId == AddressEditId) return true;
        foreach (var segment in target.Path)
        {
            if (segment.AutomationId == AddressEditId) return true;
            if (segment.ClassName is { Length: > 0 } cls && AddressClasses.Any(a => cls.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // Extra signal: Explorer itself, by name (a browser's own "Address and search bar" is not Explorer's and stays unaffected).
        return target.Process.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
               (target.Name.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
                target.AutomationId?.Contains("address", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool HasFileNameId(string? id) =>
        id is { Length: > 0 } && FileNameIds.Any(f => string.Equals(f, id, StringComparison.OrdinalIgnoreCase));
}
