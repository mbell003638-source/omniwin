using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace OmniWin.SystemTools;

internal interface IClipboardBackend
{
    /// <summary>The clipboard's text, or null when it holds none.</summary>
    Task<string?> GetTextAsync(CancellationToken ct);

    Task SetTextAsync(string text, CancellationToken ct);
}

/// <summary>Direct Win32 clipboard access (CF_UNICODETEXT). Used only by the explicit clipboard tools.</summary>
internal sealed class Win32Clipboard : IClipboardBackend
{
    private const uint CfUnicodeText = 13;

    public Task<string?> GetTextAsync(CancellationToken ct) => Task.Run(() => GetText(ct), ct);

    public Task SetTextAsync(string text, CancellationToken ct) => Task.Run(() => SetText(text, ct), ct);

    private static unsafe string? GetText(CancellationToken ct)
    {
        OpenWithRetry(ct);
        try
        {
            if (!PInvoke.IsClipboardFormatAvailable(CfUnicodeText)) return null;
            var handle = PInvoke.GetClipboardData(CfUnicodeText);
            if (handle.IsNull) return null;
            var global = new HGLOBAL((void*)handle.Value);
            var pointer = PInvoke.GlobalLock(global);
            if (pointer is null) return null;
            try
            {
                return Marshal.PtrToStringUni((IntPtr)pointer);
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

    private static unsafe void SetText(string text, CancellationToken ct)
    {
        OpenWithRetry(ct);
        try
        {
            if (!PInvoke.EmptyClipboard()) throw new InvalidOperationException("The clipboard could not be cleared.");
            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var global = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, bytes);
            if (global.IsNull) throw new OutOfMemoryException("The clipboard text could not be allocated.");

            var pointer = (char*)PInvoke.GlobalLock(global);
            if (pointer is null)
            {
                PInvoke.GlobalFree(global);
                throw new InvalidOperationException("The clipboard memory could not be locked.");
            }

            text.AsSpan().CopyTo(new Span<char>(pointer, text.Length));
            pointer[text.Length] = '\0';
            PInvoke.GlobalUnlock(global);

            if (PInvoke.SetClipboardData(CfUnicodeText, new HANDLE(global.Value)).IsNull)
            {
                PInvoke.GlobalFree(global);
                throw new InvalidOperationException("Windows refused the clipboard text.");
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static void OpenWithRetry(CancellationToken ct)
    {
        // Another app may hold the clipboard for a few ms. Bounded polling (10 x 10 ms), the only wait in this class.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (PInvoke.OpenClipboard(HWND.Null)) return;
            // F9: a bounded, cancellable wait. It stays synchronous on purpose: OpenClipboard and CloseClipboard must run on the
            // same thread, so an async wait that may resume on another thread would break the pair.
            if (ct.WaitHandle.WaitOne(10)) ct.ThrowIfCancellationRequested();
        }

        throw new InvalidOperationException("The clipboard is in use by another app.");
    }
}
