using OmniWin.Core;

namespace OmniWin.Workflows;

public enum WaitRoute { Desktop, Browser, Mixed }

/// <summary>
/// Chooses the engine a <c>wait_for</c> condition runs on. Refs and scopes say where they belong: browser refs start with
/// "b", tab ids with "t"; desktop refs with "e" or "o", window ids with "w". A condition that names nothing specific
/// (a plain text or element wait, a window opening) runs on the desktop. A condition that mixes the two is refused.
/// </summary>
public static class ConditionRouter
{
    public static WaitRoute Route(Expectation condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition switch
        {
            UrlContains => WaitRoute.Browser,
            ElementGone g => OfRef(g.Ref),
            ValueEquals v => OfRef(v.Ref),
            FocusOn f => OfRef(f.Ref),
            ToggleIs t => OfRef(t.Ref),
            WindowClosed c => OfRef(c.Window),
            ElementAppears a => OfRef(a.Within),
            TextAppears t => OfRef(t.Within),
            AllOf all => Combine(all.Items),
            AnyOf any => Combine(any.Items),
            _ => WaitRoute.Desktop,
        };
    }

    /// <summary>The tab named by a browser condition, when it names one.</summary>
    public static string? TabOf(Expectation condition) => condition switch
    {
        UrlContains u => u.Tab,
        AllOf all => all.Items.Select(TabOf).FirstOrDefault(t => t is not null),
        AnyOf any => any.Items.Select(TabOf).FirstOrDefault(t => t is not null),
        _ => null,
    };

    private static WaitRoute OfRef(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return WaitRoute.Desktop;
        return reference[0] is 'b' or 't' ? WaitRoute.Browser : WaitRoute.Desktop;
    }

    private static WaitRoute Combine(IReadOnlyList<Expectation> items)
    {
        var hasDesktop = false;
        var hasBrowser = false;
        foreach (var item in items)
        {
            switch (Route(item))
            {
                case WaitRoute.Desktop:
                    // A condition that names nothing (window opened) is neutral next to a browser one only if it is explicit.
                    hasDesktop = true;
                    break;
                case WaitRoute.Browser:
                    hasBrowser = true;
                    break;
                case WaitRoute.Mixed:
                    return WaitRoute.Mixed;
            }
        }

        return hasDesktop && hasBrowser ? WaitRoute.Mixed : hasBrowser ? WaitRoute.Browser : WaitRoute.Desktop;
    }
}
