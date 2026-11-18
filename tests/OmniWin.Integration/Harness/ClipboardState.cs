using System.Runtime.InteropServices;

namespace OmniWin.Integration.Harness;

/// <summary>
/// The harness's own read of the clipboard (Win32, not OmniWin), so a test or benchmark task that must use the clipboard can put
/// the owner's text back exactly afterwards. Text is held in memory only and never written anywhere. A clipboard that holds
/// something other than text (an image, files) cannot be put back by text tools, so the caller must not touch it at all.
/// No test-framework dependency, so the benchmark runner can link this file.
/// </summary>
public static class ClipboardState
{
    private const uint CfUnicodeText = 13;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern int CountClipboardFormats();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    /// <summary>
    /// Reads the clipboard. True with <paramref name="text"/> = the text (null when the clipboard is empty). False, with the reason,
    /// when it holds something that is not text or cannot be opened: the caller must then leave the clipboard alone.
    /// </summary>
    public static bool TryRead(out string? text, out string why)
    {
        text = null;
        if (!Open())
        {
            why = "the clipboard could not be opened (another program holds it)";
            return false;
        }

        try
        {
            var formats = CountClipboardFormats();
            if (formats == 0)
            {
                why = "";
                return true;
            }

            if (!IsClipboardFormatAvailable(CfUnicodeText))
            {
                why = "the clipboard holds something that is not text, which text tools cannot put back";
                return false;
            }

            var handle = GetClipboardData(CfUnicodeText);
            var pointer = handle == IntPtr.Zero ? IntPtr.Zero : GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                why = "the clipboard text could not be read";
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? "";
            }
            finally
            {
                GlobalUnlock(handle);
            }

            why = "";
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Empties the clipboard again (it was empty before the caller used it). False when it could not be opened.</summary>
    public static bool Empty()
    {
        if (!Open()) return false;
        try
        {
            return EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool Open()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(25);
        }

        return false;
    }
}
