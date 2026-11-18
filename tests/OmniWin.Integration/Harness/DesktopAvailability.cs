using System.Runtime.InteropServices;

namespace OmniWin.Integration.Harness;

/// <summary>
/// Is a connected, interactive desktop attached to this session? A disconnected or locked Remote Desktop session has no input
/// desktop and reports a 640x480 placeholder screen; real input and window reads cannot work there. No test-framework dependency,
/// so the benchmark runner can link this file.
/// </summary>
public static class DesktopAvailability
{
    private const uint DesktopSwitchDesktop = 0x0100;

    /// <summary>The placeholder screen of a session with no display attached.</summary>
    public const int PlaceholderPixels = 640 * 480;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    // Fails with access denied when this process cannot reach the screen the user is looking at (lock screen, secure prompt).
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SmRemoteSession = 0x1000;

    /// <summary>True when this process runs in a Remote Desktop session (GetSystemMetrics SM_REMOTESESSION), judged by Windows, not by what a page shows.</summary>
    public static bool IsRemoteSession() => GetSystemMetrics(SmRemoteSession) != 0;

    /// <summary>True when the screen is larger than the 640x480 placeholder. Judged by area, because a narrow Remote Desktop window (for example 604x1256) is a real screen.</summary>
    public static bool IsUsableScreen(int width, int height) => (long)width * height > PlaceholderPixels;

    /// <summary>Checks this session. <paramref name="reason"/> says why not, in words for a person.</summary>
    public static bool IsAvailable(out string reason)
    {
        var handle = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (handle == IntPtr.Zero)
        {
            reason = "no input desktop is attached (the session is disconnected or locked)";
            return false;
        }

        CloseDesktop(handle);
        if (!GetCursorPos(out _))
        {
            reason = "the screen cannot be reached (the cursor position is denied: a lock screen or secure prompt may be showing)";
            return false;
        }

        int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
        if (!IsUsableScreen(width, height))
        {
            reason = $"the primary screen is {width}x{height}, the placeholder of a session with no display";
            return false;
        }

        reason = "";
        return true;
    }

    public static bool IsAvailable() => IsAvailable(out _);
}
