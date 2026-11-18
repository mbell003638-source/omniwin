using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace OmniWin.Desktop;

/// <summary>
/// What a paste would insert. <see cref="Readable"/> is false when the clipboard could not be opened or read: the caller
/// then cannot know what would be pasted. <see cref="Text"/> is null when the clipboard holds no text (a picture, files).
/// </summary>
internal readonly record struct ClipboardSnapshot(bool Readable, string? Text);

/// <summary>
/// F1: reads the clipboard text only to check a paste into a command window with the same delete-command check that typed
/// text gets. The text is never stored or logged. Replaceable in tests through <see cref="Reader"/>.
/// </summary>
internal static class PasteClipboard
{
    private const uint CfUnicodeText = 13;

    /// <summary>More than this is not read (a command line cannot be longer in any shell OmniWin checks).</summary>
    internal const int MaxCharacters = 65536;

    internal static Func<ClipboardSnapshot> Reader { get; set; } = ReadSystemClipboard;

    private static unsafe ClipboardSnapshot ReadSystemClipboard()
    {
        // Another app may hold the clipboard for a few ms. Bounded polling (10 x 10 ms).
        var opened = false;
        for (var attempt = 0; attempt < 10 && !opened; attempt++)
        {
            opened = PInvoke.OpenClipboard(HWND.Null);
            if (!opened) SpinWait.SpinUntil(static () => false, 10);
        }

        if (!opened) return new ClipboardSnapshot(false, null);

        try
        {
            if (!PInvoke.IsClipboardFormatAvailable(CfUnicodeText)) return new ClipboardSnapshot(true, null);
            var handle = PInvoke.GetClipboardData(CfUnicodeText);
            if (handle.IsNull) return new ClipboardSnapshot(false, null);
            var global = new HGLOBAL((void*)handle.Value);
            var pointer = PInvoke.GlobalLock(global);
            if (pointer is null) return new ClipboardSnapshot(false, null);
            try
            {
                var text = Marshal.PtrToStringUni((IntPtr)pointer);
                if (text is { Length: > MaxCharacters }) text = text[..MaxCharacters];
                return new ClipboardSnapshot(true, text);
            }
            finally
            {
                PInvoke.GlobalUnlock(global);
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }
}
