using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

namespace OmniWin.Server.Hosting;

internal static class Platform
{
    /// <summary>True when the process has a full administrator token (elevated, or UAC disabled).</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>DPI awareness of the calling thread: "per_monitor_v2" when the manifest took effect.</summary>
    public static string DpiAwareness()
    {
        var context = PInvoke.GetThreadDpiAwarenessContext();
        if (PInvoke.AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        {
            return "per_monitor_v2";
        }

        return PInvoke.GetAwarenessFromDpiAwarenessContext(context) switch
        {
            DPI_AWARENESS.DPI_AWARENESS_UNAWARE => "unaware",
            DPI_AWARENESS.DPI_AWARENESS_SYSTEM_AWARE => "system",
            DPI_AWARENESS.DPI_AWARENESS_PER_MONITOR_AWARE => "per_monitor_v1",
            _ => "unknown",
        };
    }
}
