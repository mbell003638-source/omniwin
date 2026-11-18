using System.Globalization;
using System.Text;
using OmniWin.Core;

namespace OmniWin.SystemTools;

/// <summary>
/// One-line text formats for the read tools (proposal Q7). Anything that comes from another app or from the system
/// (app names, device names, adapter names, clipboard text) sits under the untrusted-content header or inside quotes.
/// </summary>
public static class SystemFormat
{
    public const int MaxAppsListed = 300;

    public static string AppsList(IReadOnlyList<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var text = new StringBuilder(ResultFormatter.UntrustedHeader);
        foreach (var app in apps.Take(MaxAppsListed))
        {
            text.Append('\n').Append(ResultFormatter.Quote(app.Name, ResultFormatter.MaxNameChars))
                .Append(" id=").Append(ResultFormatter.Quote(app.LaunchId, 200));
        }

        text.Append("\napps: ").Append(apps.Count.ToString(CultureInfo.InvariantCulture));
        if (apps.Count > MaxAppsListed)
        {
            text.Append(" (").Append((apps.Count - MaxAppsListed).ToString(CultureInfo.InvariantCulture)).Append(" more: narrow with filter)");
        }

        return text.ToString();
    }

    public static string Audio(AudioState state, Timing timing)
    {
        ArgumentNullException.ThrowIfNull(state);
        var text = new StringBuilder("audio volume=").Append(state.VolumePercent.ToString(CultureInfo.InvariantCulture))
            .Append(" muted=").Append(state.Muted ? "true" : "false")
            .Append(" device=").Append(ResultFormatter.Quote(state.DeviceName, ResultFormatter.MaxNameChars))
            .Append(' ');
        ResultFormatter.AppendTiming(text, timing);
        return text.ToString();
    }

    /// <summary>Only <c>network</c> carries names from outside OmniWin (adapter names), so only it gets the header.</summary>
    public static string SystemInfo(string kind, string body)
    {
        var text = new StringBuilder();
        if (string.Equals(kind?.Trim(), "network", StringComparison.OrdinalIgnoreCase))
        {
            text.Append(ResultFormatter.UntrustedHeader).Append('\n');
        }

        return text.Append(body).ToString();
    }

    /// <summary>
    /// First line has the length and <c>risk=risky</c> but never the text, so the audit line cannot hold clipboard content.
    /// The untrusted header comes before the text.
    /// </summary>
    public static string Clipboard(ClipboardText clip, Timing timing)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var text = new StringBuilder("clipboard chars=").Append(clip.Chars.ToString(CultureInfo.InvariantCulture));
        if (clip.Truncated) text.Append(" truncated=true");
        text.Append(" risk=risky ");
        ResultFormatter.AppendTiming(text, timing);
        return text.Append('\n').Append(ResultFormatter.UntrustedHeader).Append('\n').Append(clip.Text).ToString();
    }
}
