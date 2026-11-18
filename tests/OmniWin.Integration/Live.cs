using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// Switch for tests that send real input, show windows or change the foreground. They run only when the owner starts the live session
/// (OMNIWIN_LIVE_SESSION=1) AND a connected interactive desktop is attached. Otherwise they skip with a message and never hang or fail.
/// </summary>
public static class Live
{
    public static bool RequestedByOwner => Environment.GetEnvironmentVariable("OMNIWIN_LIVE_SESSION") == "1";

    /// <summary>Why the live tests are not running, or null when they can run.</summary>
    public static string? WhyNot
    {
        get
        {
            if (!RequestedByOwner) return "OMNIWIN_LIVE_SESSION=1 is not set.";
            return DesktopAvailability.IsAvailable(out var reason) ? null : "No connected desktop: " + reason + ".";
        }
    }

    public static bool Enabled => WhyNot is null;

    public const string Skip =
        "Live test: skipped unless OMNIWIN_LIVE_SESSION=1 is set AND a connected, unlocked desktop is attached " +
        "(a disconnected Remote Desktop session has none). It sends real input, shows windows and changes the foreground, " +
        "so run it only in the live session with the owner present (it holds the Local\\OmniWin.UiInputTestLock mutex).";
}
