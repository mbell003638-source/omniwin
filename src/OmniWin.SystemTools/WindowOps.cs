using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace OmniWin.SystemTools;

internal readonly record struct WindowState(bool Exists, bool Minimized, bool Maximized, int X, int Y, int Width, int Height);

internal readonly record struct WorkArea(int X, int Y, int Width, int Height);

/// <summary>Direct window operations by handle. Everything here is a plain Win32 call; no input is injected.</summary>
internal interface IWindowOps
{
    WindowState Read(long hwnd);

    void Minimize(long hwnd);

    void Maximize(long hwnd);

    void Restore(long hwnd);

    void Place(long hwnd, int x, int y, int width, int height);

    /// <summary>The usable area of the monitor that holds most of the window (physical pixels).</summary>
    WorkArea WorkAreaOf(long hwnd);

    /// <summary>Asks the window to close (WM_CLOSE), exactly like its close button.</summary>
    void RequestClose(long hwnd);
}

internal sealed class Win32WindowOps : IWindowOps
{
    public WindowState Read(long hwnd)
    {
        var handle = new HWND((nint)hwnd);
        if (!PInvoke.IsWindow(handle)) return new WindowState(false, false, false, 0, 0, 0, 0);
        PInvoke.GetWindowRect(handle, out var rect);
        return new WindowState(true, PInvoke.IsIconic(handle), PInvoke.IsZoomed(handle), rect.left, rect.top, rect.Width, rect.Height);
    }

    public void Minimize(long hwnd) => PInvoke.ShowWindow(new HWND((nint)hwnd), SHOW_WINDOW_CMD.SW_MINIMIZE);

    public void Maximize(long hwnd) => PInvoke.ShowWindow(new HWND((nint)hwnd), SHOW_WINDOW_CMD.SW_MAXIMIZE);

    public void Restore(long hwnd) => PInvoke.ShowWindow(new HWND((nint)hwnd), SHOW_WINDOW_CMD.SW_RESTORE);

    public void Place(long hwnd, int x, int y, int width, int height)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        if (!PInvoke.SetWindowPos(new HWND((nint)hwnd), HWND.Null, x, y, width, height, flags))
        {
            throw new InvalidOperationException("Windows refused to move or resize the window.");
        }
    }

    public unsafe WorkArea WorkAreaOf(long hwnd)
    {
        var monitor = PInvoke.MonitorFromWindow(new HWND((nint)hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("The monitor of the window could not be read.");
        return new WorkArea(info.rcWork.left, info.rcWork.top, info.rcWork.Width, info.rcWork.Height);
    }

    public void RequestClose(long hwnd)
    {
        if (!PInvoke.PostMessage(new HWND((nint)hwnd), PInvoke.WM_CLOSE, 0, 0))
        {
            throw new InvalidOperationException("The close request could not be sent to the window.");
        }
    }
}
