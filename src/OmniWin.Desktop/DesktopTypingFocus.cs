using System.Runtime.InteropServices;
using OmniWin.Core;

namespace OmniWin.Desktop;

internal sealed record VerifiedNativeEditor(long Window, long Handle, int Pid, string ClassName);

internal readonly record struct NativeTypingFocus(long Foreground, long Focus, long Root, int Pid, string ClassName);

/// <summary>Owner-approved between-batch focus policy for positively identified native editors.</summary>
internal static class DesktopTypingFocus
{
    internal static bool IsCandidate(string process, string role, string nativeClass, bool password) =>
        !password && (role is "edit" or "document") && !IsBrowser(process) &&
        (nativeClass.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
         nativeClass.Equals("RichEditD2DPT", StringComparison.OrdinalIgnoreCase) ||
         nativeClass.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase));

    private static bool IsBrowser(string process) => process.ToLowerInvariant() is
        "chrome.exe" or "msedge.exe" or "chromium.exe" or "firefox.exe" or "brave.exe" or "opera.exe" or "vivaldi.exe";

    internal static void CheckBatch(bool firstBatch, VerifiedNativeEditor? editor,
        Action fullCheck, Func<NativeTypingFocus> readNativeFocus)
    {
        // Windowless/custom/browser controls never get this shortcut. The first batch always uses UIA.
        if (firstBatch || editor is null)
        {
            fullCheck();
            return;
        }

        var native = readNativeFocus();
        if (native.Foreground != editor.Window || native.Focus != editor.Handle || native.Root != editor.Window ||
            native.Pid != editor.Pid || !native.ClassName.Equals(editor.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            throw new DesktopFault(ActStatus.Blocked, ReasonCodes.FocusChanged,
                "Keyboard focus changed before the next native-editor input batch.");
        }
    }
}

internal sealed partial class UiaDesktop
{
    private VerifiedNativeEditor? VerifyNativeEditor(DesktopWindow window, LiveElement live)
    {
        using var diagnostic = DesktopDiagnostics.Measure("VerifyNativeEditor");
        var handle = live.NativeHandle;
        if (handle == 0 || NativeDesktop.ProcessId(handle) != live.Sample.Pid ||
            live.Sample.Pid != window.Sample.Pid || NativeDesktop.Root(handle) != window.Sample.Hwnd)
            return null;

        var nativeClass = NativeDesktop.NativeClassName(handle);
        if (!DesktopTypingFocus.IsCandidate(window.Sample.Process, live.Sample.Role, nativeClass, live.Sample.Password) ||
            !nativeClass.Equals(live.Sample.ClassName, StringComparison.OrdinalIgnoreCase))
            return null;

        // A nonzero HWND alone does not prove that this logical UIA control owns that native editor.
        // Require the handle's UIA element to be this exact target, excluding shared-host fragments.
        try
        {
            var nativeElement = _uia.ElementFromHandle((nint)handle);
            return nativeElement is not null && _uia.CompareElements(live.Element, nativeElement) != 0
                ? new VerifiedNativeEditor(window.Sample.Hwnd, handle, live.Sample.Pid, nativeClass) : null;
        }
        catch (COMException)
        {
            // Optional shortcut could not be established. The regular full checks still run before
            // every batch, and still propagate provider/focus failures before any keystrokes.
            return null;
        }
    }

    private static NativeTypingFocus ReadNativeTypingFocus()
    {
        using var diagnostic = DesktopDiagnostics.Measure("RequireNativeEditorFocus");
        var focus = NativeDesktop.FocusHandle();
        return new NativeTypingFocus(NativeDesktop.Foreground, focus, NativeDesktop.Root(focus),
            NativeDesktop.ProcessId(focus), NativeDesktop.NativeClassName(focus));
    }

    private bool PacketEditorAcknowledged(DesktopWindow window, LiveElement live, VerifiedNativeEditor editor,
        int expectedCaret, long sentAt, Func<string?> stopReason, CancellationToken ct)
    {
        using var diagnostic = DesktopDiagnostics.Measure("RichEdit.PacketProgress");
        void FullFocusCheck() => RequireKeyboardFocus(window, live);
        return PacketEditorProgress.TryAcknowledge(expectedCaret, () =>
        {
            return NativeDesktop.TryGetPacketEditorSelection(editor.Handle, out var start, out var end)
                ? (start, end) : null;
        }, () =>
        {
            CheckInputBoundary(window, stopReason, ct);
            DesktopTypingFocus.CheckBatch(false, editor, FullFocusCheck, ReadNativeTypingFocus);
        }, () => System.Diagnostics.Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds >= 250);
    }
}
