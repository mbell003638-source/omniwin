using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32.SafeHandles;
using OmniWin.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Security;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace OmniWin.Desktop;

internal static class NativeDesktop
{
    // WinUI can report a focused child as foreground. Indexed windows and activity ownership use
    // the top-level identity; FocusHandle deliberately retains the actual keyboard-focus child.
    internal static long Foreground => Root((nint)PInvoke.GetForegroundWindow());

    internal static Point? CursorPosition() => PInvoke.GetCursorPos(out var point) ? new Point(point.X, point.Y) : null;

    internal static ScreenRect VirtualScreen => new(
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));

    internal static unsafe bool OnInputDesktop()
    {
        using var input = PInvoke.OpenInputDesktop_SafeHandle(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        var current = PInvoke.GetThreadDesktop(PInvoke.GetCurrentThreadId());
        if (input.IsInvalid || current.IsNull)
        {
            return false;
        }

        return DesktopName((HANDLE)input.DangerousGetHandle()) is { } name &&
               string.Equals(name, DesktopName(new HANDLE(current.Value)), StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe string? DesktopName(HANDLE desktop)
    {
        var buffer = stackalloc char[256];
        return PInvoke.GetUserObjectInformation(desktop, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, buffer, 512, null)
            ? new string(buffer) : null;
    }

    internal static unsafe bool IsTopLevelWindow(long hwnd)
    {
        var handle = Handle(hwnd);
        if (hwnd == 0 || !PInvoke.IsWindowVisible(handle) || handle == PInvoke.GetDesktopWindow() ||
            handle == PInvoke.GetShellWindow() || PInvoke.GetAncestor(handle, GET_ANCESTOR_FLAGS.GA_ROOT) != handle)
        {
            return false;
        }

        uint cloaked = 0;
        return PInvoke.DwmGetWindowAttribute(handle, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Value < 0 ||
               cloaked == 0;
    }

    internal static int ProcessId(long hwnd)
    {
        PInvoke.GetWindowThreadProcessId(Handle(hwnd), out var pid);
        return (int)pid;
    }

    internal static (string Name, long Start, bool Elevated) ReadProcess(int pid)
    {
        if (pid <= 0)
        {
            return ("unknown", 0, true);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }

            var elevated = IsElevated(pid);
            return (name, elevated ? 0 : process.StartTime.ToFileTimeUtc(), elevated);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return ("unknown", 0, true);
        }
    }

    private static unsafe bool IsElevated(int pid)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        return process.IsInvalid || IsElevated(process);
    }

    private static unsafe bool IsElevated(SafeFileHandle process)
    {
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return true;
        }

        using (token)
        {
            uint elevation = 0;
            return !PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, &elevation, sizeof(uint), out _) ||
                   elevation != 0;
        }
    }

    internal static ProcessIdentity? OpenProcessIdentity(int pid)
    {
        if (pid <= 0) return null;
        SafeFileHandle? handle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION |
            PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE, false, (uint)pid);
        try
        {
            if (handle.IsInvalid || !PInvoke.GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
            var path = new char[32768];
            var length = (uint)path.Length;
            if (!PInvoke.QueryFullProcessImageName(handle, 0, path, ref length)) return null;
            var name = Path.GetFileName(new string(path, 0, (int)length));
            var start = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
            var identity = new ProcessIdentity(handle, (name, start, IsElevated(handle)));
            if (!identity.IsAlive) return null;
            handle = null;
            return identity;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal sealed class ProcessIdentity(SafeFileHandle handle, (string Name, long Start, bool Elevated) info) : IDisposable
    {
        internal (string Name, long Start, bool Elevated) Info { get; } = info;

        // This handle stays bound to the original process even after Windows reuses its PID.
        internal bool IsAlive => !handle.IsClosed && !handle.IsInvalid &&
            PInvoke.WaitForSingleObject(handle, 0) == WAIT_EVENT.WAIT_TIMEOUT;

        public void Dispose() => handle.Dispose();
    }

    internal static string Title(long hwnd)
    {
        Span<char> text = stackalloc char[512];
        return new string(text[..PInvoke.GetWindowText(Handle(hwnd), text)]);
    }

    internal static string NativeClassName(long hwnd)
    {
        Span<char> name = stackalloc char[256];
        return new string(name[..PInvoke.GetClassName(Handle(hwnd), name)]);
    }

    internal static unsafe long Owner(long hwnd) => (long)PInvoke.GetWindow(Handle(hwnd), GET_WINDOW_CMD.GW_OWNER).Value;

    internal static bool Enabled(long hwnd) => PInvoke.IsWindowEnabled(Handle(hwnd));
    internal static bool Minimized(long hwnd) => PInvoke.IsIconic(Handle(hwnd));
    internal static bool IsWindow(long hwnd) => PInvoke.IsWindow(Handle(hwnd));

    internal static IReadOnlyList<long> TopLevelWindows(CancellationToken ct)
    {
        var windows = new List<long>();
        var success = PInvoke.EnumWindows((handle, _) =>
        {
            if (ct.IsCancellationRequested) return false;
            windows.Add((long)(nint)handle);
            return true;
        }, default);
        ct.ThrowIfCancellationRequested();
        if (!success) throw new Win32Exception();
        return windows;
    }

    internal static ScreenRect Bounds(long hwnd) => PInvoke.GetWindowRect(Handle(hwnd), out var rect)
        ? Rect(rect.left, rect.top, rect.right, rect.bottom)
        : Empty;

    internal static ScreenRect Empty { get; } = new(0, 0, 0, 0);
    internal static bool HasArea(ScreenRect rect) => rect.Width > 0 && rect.Height > 0;

    internal static ScreenRect Rect(int left, int top, int right, int bottom)
    {
        var width = (long)right - left;
        var height = (long)bottom - top;
        return width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue
            ? new(left, top, (int)width, (int)height)
            : Empty;
    }

    internal static ScreenRect Intersect(ScreenRect a, ScreenRect b) =>
        Rect(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Min(a.X + a.Width, b.X + b.Width),
            Math.Min(a.Y + a.Height, b.Y + b.Height));

    internal static bool OnMonitor(ScreenRect rect)
    {
        var native = new RECT(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
        return HasArea(rect) && !PInvoke.MonitorFromRect(in native, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL).IsNull;
    }

    internal static bool Contains(ScreenRect rect, Point point) =>
        point.X >= rect.X && point.X < (long)rect.X + rect.Width && point.Y >= rect.Y && point.Y < (long)rect.Y + rect.Height;

    private static class LegacyKey
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

        internal static void Tap(byte vk)
        {
            keybd_event(vk, 0, 0, 0);
            keybd_event(vk, 0, 2, 0);
        }
    }

    /// <summary>
    /// Waits until the thread that owns keyboard focus has consumed the input sent so far. Windows keeps the
    /// pending VK_PACKET character per thread, so a second character queued while the first is still waiting
    /// (a control that is busy right after activation) makes both come out as the second one. A sent message
    /// is dispatched only when the thread pumps; the second ping is dispatched after the thread has handled
    /// the key message that was already queued when the first returned.
    /// </summary>
    internal static void WaitForFocusThreadToDrain()
    {
        var target = FocusHandle();
        if (target == 0) target = Foreground;
        if (target == 0) return;
        for (var i = 0; i < 2; i++)
        {
            PInvoke.SendMessageTimeout(Handle(target), PInvoke.WM_NULL, default, default,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 500, out _);
        }
    }

    /// <summary>The caret of the focused native Edit control, or null when it has none OmniWin can read.</summary>
    internal static (int Start, int End)? NativeCaret() =>
        FocusHandle() is var focus && focus != 0 && TryGetEditSelection(focus, out var s, out var e) ? (s, e) : null;

    /// <summary>
    /// Waits until the typed scalar was really consumed. A native Edit proves it by moving its caret; any other
    /// control gets the drain barrier plus a short settle time for the first scalar, when Windows finishes
    /// per-thread text-service setup (during which a later packet overwrites the earlier one).
    /// </summary>
    internal static void AwaitTypedScalar((int Start, int End)? before, bool first, int timeoutMs = 1000) =>
        AwaitTypedScalar(before, first, timeoutMs, WaitForFocusThreadToDrain, NativeCaret, Thread.Sleep);

    /// <summary>The same wait with its three native parts passed in (drain the focus thread, read the caret, sleep), so it is tested without a window.</summary>
    internal static void AwaitTypedScalar((int Start, int End)? before, bool first, int timeoutMs,
        Action drain, Func<(int Start, int End)?> caret, Action<int> sleep)
    {
        drain();
        if (before is { } b)
        {
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < timeoutMs)
            {
                if (caret() is { } now && (now.Start != b.Start || now.End != b.End)) return;
                sleep(2);
            }
        }
        else if (first)
        {
            sleep(120);
            drain();
        }
    }

    // Clients that forward keys to another computer. A synthetic key tap must never be sent while one of them is in front.
    private static readonly string[] ForwardingClients = ["mstsc", "msrdc", "vmconnect", "VirtualBoxVM", "vmware", "vmware-vmx"];

    internal static bool IsForwardingClient(string? processName) =>
        processName is not null && ForwardingClients.Contains(
            processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Seams for tests: does the window answer a no-op message in time, and which process owns it.</summary>
    internal static Func<long, bool> Responds { get; set; } = static hwnd =>
        PInvoke.SendMessageTimeout(Handle(hwnd), PInvoke.WM_NULL, default, default, SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 100, out _) != 0;

    internal static Func<long, string?> ProcessNameOf { get; set; } = static hwnd =>
    {
        try
        {
            var pid = ProcessId(hwnd);
            if (pid == 0) return null;
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    };

    /// <summary>Seam for tests: the window in front right now.</summary>
    internal static Func<long> FrontWindow { get; set; } = static () => (long)(nint)PInvoke.GetForegroundWindow();

    /// <summary>The F24 tap check, made again immediately before the tap against whatever window is in front at that moment.</summary>
    internal static bool TapAllowedNow() => FocusFallbacksAllowed(FrontWindow()).Tap;

    /// <summary>
    /// May the two focus fallbacks (input-queue attach, F24 tap) run against the window that is in front now?
    /// Not when that window is hung (an attach to a hung thread could block the UI Automation worker), and the tap
    /// not when it belongs to a remote-desktop or virtual-machine client (the key would go to another computer).
    /// </summary>
    internal static (bool Attach, bool Tap) FocusFallbacksAllowed(long foreground)
    {
        if (foreground == 0) return (false, true);
        if (!Responds(foreground)) return (false, false);
        return (true, !IsForwardingClient(ProcessNameOf(foreground)));
    }

    internal static unsafe bool TryForeground(long hwnd, out bool windowInput, out bool keyTapped, Action? boundary = null)
    {
        windowInput = false;
        keyTapped = false;
        var rawForeground = (long)(nint)PInvoke.GetForegroundWindow();
        if (Root(rawForeground) == hwnd)
        {
            if (rawForeground == hwnd) return Foreground == hwnd;
            var raisedCurrentFrame = RaiseFrame(hwnd, ref windowInput, boundary);
            return raisedCurrentFrame && Foreground == hwnd;
        }

        if (Minimized(hwnd))
        {
            boundary?.Invoke();
            windowInput |= PInvoke.ShowWindowAsync(Handle(hwnd), SHOW_WINDOW_CMD.SW_RESTORE);
        }

        boundary?.Invoke();
        var activated = PInvoke.SetForegroundWindow(Handle(hwnd));
        windowInput |= activated;
        var allowed = (Attach: false, Tap: false);
        if (!activated && Foreground != hwnd)
            allowed = FocusFallbacksAllowed((long)(nint)PInvoke.GetForegroundWindow());

        if (!activated && Foreground != hwnd && allowed.Attach)
        {
            // Windows refuses focus changes from a process that did not receive the last input.
            // Joining the current foreground thread's input queue for one call is the documented
            // way for an automation tool acting for the user to be allowed to change focus.
            // The window in front answered a no-op message just now, so its thread is not hung.
            var fgThread = PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), out _);
            var self = PInvoke.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != self && PInvoke.AttachThreadInput(self, fgThread, true))
            {
                try
                {
                    boundary?.Invoke();
                    activated = PInvoke.SetForegroundWindow(Handle(hwnd));
                    windowInput |= activated;
                }
                finally { PInvoke.AttachThreadInput(self, fgThread, false); }
            }
        }

        if (!activated && Foreground != hwnd && allowed.Tap)
        {
            // Last resort: a synthetic tap of F24 makes this process the last input receiver, which is what
            // Windows' focus rule checks. The key goes to whatever window is in front (a tool that binds F24
            // would see it), so it is skipped for hung windows and for clients that forward keys elsewhere,
            // and it is reported as a key press.
            boundary?.Invoke();
            // The window in front can have changed since the first look (the attach step above takes time): look again right
            // before the key goes out. No tap when it is a client that forwards keys elsewhere or a window that does not answer.
            if (TapAllowedNow())
            {
                LegacyKey.Tap(0x87);
                keyTapped = true;
                windowInput = true;
                activated = PInvoke.SetForegroundWindow(Handle(hwnd));
            }
        }

        if (!activated && Foreground != hwnd)
        {
            // WinUI editors can own keyboard activation on a different thread from their frame.
            // Only use that thread's focus child when it is still inside this exact target window.
            var thread = PInvoke.GetWindowThreadProcessId(Handle(hwnd), out var pid);
            var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
            if (thread != 0 && PInvoke.GetGUIThreadInfo(thread, ref info) && !info.hwndFocus.IsNull)
            {
                var child = (long)(nint)info.hwndFocus;
                if (child != hwnd && Root(child) == hwnd && ProcessId(child) == (int)pid &&
                    PInvoke.IsWindowVisible(info.hwndFocus) && PInvoke.IsWindowEnabled(info.hwndFocus))
                {
                    boundary?.Invoke();
                    activated = PInvoke.SetForegroundWindow(info.hwndFocus);
                    windowInput |= activated;
                }
            }
        }

        if (activated && Foreground != hwnd)
        {
            // Cross-thread activation is asynchronous. A bounded no-op message lets the target process
            // its activation notification before we verify the actual foreground window.
            PInvoke.SendMessageTimeout(Handle(hwnd), PInvoke.WM_NULL, default, default,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ERRORONEXIT, 250, out _);
        }

        rawForeground = (long)(nint)PInvoke.GetForegroundWindow();
        if (Root(rawForeground) != hwnd) return false;
        if (rawForeground == hwnd) return Foreground == hwnd;
        var raised = RaiseFrame(hwnd, ref windowInput, boundary);
        return raised && Foreground == hwnd;
    }

    // Activating a WinUI child can leave its frame behind another application's frame. Raise the
    // requested top-level frame without taking focus away from its already-authorized child.
    private static bool RaiseFrame(long hwnd, ref bool windowInput, Action? boundary) =>
        RaiseFrame(hwnd, CaptureZOrder, SetFrameAtTop, ref windowInput, boundary);

    private static bool SetFrameAtTop(long hwnd) => PInvoke.SetWindowPos(Handle(hwnd), HWND.Null /* HWND_TOP = 0 */,
        0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    internal readonly record struct ZOrderSnapshot(long[] Windows, bool Complete);

    // The delegates make native mutation accounting testable without changing the desktop.
    internal static bool RaiseFrame(long hwnd, Func<long, ZOrderSnapshot> capture, Func<long, bool> raise,
        ref bool windowInput, Action? boundary = null)
    {
        boundary?.Invoke();
        var before = capture(hwnd);
        boundary?.Invoke();
        var priorInput = windowInput;
        // A native call or its following snapshot can fail after a partial mutation.
        windowInput = true;
        var raised = raise(hwnd);
        var after = capture(hwnd);
        windowInput = priorInput || !before.Complete || !after.Complete ||
            !before.Windows.AsSpan().SequenceEqual(after.Windows);
        return raised;
    }

    private static ZOrderSnapshot CaptureZOrder(long target) => CaptureZOrder(target,
        static hwnd => (long)(nint)PInvoke.GetWindow(Handle(hwnd), GET_WINDOW_CMD.GW_HWNDFIRST),
        static hwnd => (long)(nint)PInvoke.GetWindow(Handle(hwnd), GET_WINDOW_CMD.GW_HWNDNEXT),
        static hwnd => PInvoke.IsWindow(Handle(hwnd)));

    internal static ZOrderSnapshot CaptureZOrder(long target, Func<long, long> first, Func<long, long> next,
        Func<long, bool> exists, int maximum = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        var windows = new List<long>();
        var seen = new HashSet<long>();
        // EnumWindows omits UWP surfaces. The full native order also detects owner/owned-group
        // movement when the target's immediate predecessor happens to stay the same.
        var current = first(target);
        while (current != 0 && windows.Count < maximum && seen.Add(current) && exists(current))
        {
            windows.Add(current);
            var following = next(current);
            if (following == 0)
                return new ZOrderSnapshot(windows.ToArray(), seen.Contains(target) && exists(current));
            current = following;
        }

        // Cycles, disappearing handles, and budget exhaustion cannot prove a no-op.
        return new ZOrderSnapshot(windows.ToArray(), false);
    }

    internal static long WindowAt(Point point) => (nint)PInvoke.WindowFromPoint(point);

    internal static unsafe long Root(long hwnd) => (long)PInvoke.GetAncestor(Handle(hwnd), GET_ANCESTOR_FLAGS.GA_ROOT).Value;

    internal static bool IsWithin(long parent, long child) => parent == child || PInvoke.IsChild(Handle(parent), Handle(child));

    internal static unsafe long FocusHandle()
    {
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        return PInvoke.GetGUIThreadInfo(0, ref info) ? (long)info.hwndFocus.Value : 0;
    }

    internal static unsafe bool PointerCaptured()
    {
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        return !PInvoke.GetGUIThreadInfo(0, ref info) || !info.hwndCapture.IsNull;
    }

    internal static unsafe bool TryGetEditSelection(long hwnd, out int start, out int end)
    {
        start = 0;
        end = 0;
        Span<char> className = stackalloc char[256];
        var classLength = PInvoke.GetClassName(Handle(hwnd), className);
        ReadOnlySpan<char> nativeClass = className[..classLength];
        if (!nativeClass.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
            !nativeClass.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetSelectionOffsets(hwnd, out start, out end);
    }

    // Packet acknowledgement uses native RichEdit offsets only. Keep this separate from the
    // Edit capture path, whose offsets must match the complete UIA value's newline representation.
    internal static bool TryGetPacketEditorSelection(long hwnd, out int start, out int end)
    {
        start = 0;
        end = 0;
        Span<char> className = stackalloc char[256];
        var classLength = PInvoke.GetClassName(Handle(hwnd), className);
        ReadOnlySpan<char> nativeClass = className[..classLength];
        if (!nativeClass.Equals("RichEditD2DPT", StringComparison.OrdinalIgnoreCase)) return false;

        return TryGetSelectionOffsets(hwnd, out start, out end);
    }

    private static unsafe bool TryGetSelectionOffsets(long hwnd, out int start, out int end)
    {
        start = 0;
        end = 0;
        uint selectionStart = 0;
        uint selectionEnd = 0;
        var result = PInvoke.SendMessageTimeout(Handle(hwnd), PInvoke.EM_GETSEL,
            new WPARAM((nuint)(&selectionStart)), new LPARAM((nint)(&selectionEnd)),
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG |
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ERRORONEXIT, 250, out _);
        if (result.Value == 0 || selectionStart > int.MaxValue || selectionEnd > int.MaxValue)
        {
            return false;
        }

        start = (int)selectionStart;
        end = (int)selectionEnd;
        return start <= end;
    }

    internal static bool ModifiersHeld() => Down(VIRTUAL_KEY.VK_CONTROL) || Down(VIRTUAL_KEY.VK_SHIFT) ||
        Down(VIRTUAL_KEY.VK_MENU) || Down(VIRTUAL_KEY.VK_LWIN) || Down(VIRTUAL_KEY.VK_RWIN);

    internal static bool MouseButtonsHeld() => Down(VIRTUAL_KEY.VK_LBUTTON) || Down(VIRTUAL_KEY.VK_RBUTTON) || Down(VIRTUAL_KEY.VK_MBUTTON);

    internal static unsafe bool WheelFollowsPointer
    {
        get
        {
            uint routing = 0;
            return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETMOUSEWHEELROUTING, 0, &routing, 0) &&
                routing == 2;
        }
    }

    private static bool Down(VIRTUAL_KEY key) => (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static HWND Handle(long hwnd) => new((nint)hwnd);
}
