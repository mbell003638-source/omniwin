using System.Runtime.InteropServices;
using OmniWin.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;
using Windows.Win32.UI.WindowsAndMessaging;

namespace OmniWin.Ocr;

/// <summary>A 24-bit BMP file in memory plus where its top-left pixel sits on the screen (physical pixels).</summary>
internal sealed record CapturedImage(byte[] Bmp, int Width, int Height, int OriginX, int OriginY);

internal interface IScreenCapture
{
    /// <summary>The window's full content (also when partly covered), or null when it can't be captured.</summary>
    CapturedImage? CaptureWindow(long hwnd);

    /// <summary>A screen rectangle as it looks now. Areas in <paramref name="maskOut"/> are painted black before OCR.</summary>
    CapturedImage? CaptureRegion(ScreenRect region, IReadOnlyList<ScreenRect> maskOut);

    /// <summary>The window's rectangle on the screen, or null when it is gone.</summary>
    ScreenRect? WindowRect(long hwnd);

    /// <summary>The top-level window under a screen point, or 0.</summary>
    long TopLevelWindowAt(int x, int y);

    ScreenRect VirtualScreen { get; }
}

/// <summary>GDI capture. Read-only: it copies pixels and sends no input. Nothing is written to disk.</summary>
internal sealed class GdiScreenCapture : IScreenCapture
{
    public ScreenRect VirtualScreen => new(
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));

    public ScreenRect? WindowRect(long hwnd)
    {
        var handle = new HWND((nint)hwnd);
        if (!PInvoke.IsWindow(handle) || !PInvoke.GetWindowRect(handle, out var rect)) return null;
        return new ScreenRect(rect.left, rect.top, rect.Width, rect.Height);
    }

    public unsafe long TopLevelWindowAt(int x, int y)
    {
        var hit = PInvoke.WindowFromPoint(new System.Drawing.Point(x, y));
        if (hit.IsNull) return 0;
        var root = PInvoke.GetAncestor(hit, GET_ANCESTOR_FLAGS.GA_ROOT);
        return (long)(root.IsNull ? hit.Value : root.Value);
    }

    public unsafe CapturedImage? CaptureWindow(long hwnd)
    {
        var rect = WindowRect(hwnd);
        if (rect is null || rect.Width <= 0 || rect.Height <= 0) return null;

        var handle = new HWND((nint)hwnd);
        return Capture(rect.Width, rect.Height, rect.X, rect.Y, dc =>
            PInvoke.PrintWindow(handle, dc, (PRINT_WINDOW_FLAGS)2 /* PW_RENDERFULLCONTENT */));
    }

    public CapturedImage? CaptureRegion(ScreenRect region, IReadOnlyList<ScreenRect> maskOut)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;
        return Capture(region.Width, region.Height, region.X, region.Y, dc =>
        {
            var screen = PInvoke.GetDC(HWND.Null);
            try
            {
                var copied = PInvoke.BitBlt(dc, 0, 0, region.Width, region.Height, screen, region.X, region.Y, ROP_CODE.SRCCOPY);
                if (!copied) return false;
            }
            finally
            {
                PInvoke.ReleaseDC(HWND.Null, screen);
            }

            // Forbidden windows (secure desktop, sign-in, OmniWin itself) are painted over before any OCR runs.
            PaintMasks(dc, region, maskOut);

            return true;
        });
    }

    /// <summary>Paints each mask rectangle (screen coordinates) black on a bitmap whose top-left is <paramref name="region"/>'s origin.</summary>
    internal static unsafe void PaintMasks(HDC dc, ScreenRect region, IReadOnlyList<ScreenRect> maskOut)
    {
        var black = PInvoke.CreateSolidBrush(new COLORREF(0));
        try
        {
            foreach (var mask in maskOut)
            {
                var x0 = Math.Max(mask.X, region.X) - region.X;
                var y0 = Math.Max(mask.Y, region.Y) - region.Y;
                var x1 = Math.Min(mask.X + mask.Width, region.X + region.Width) - region.X;
                var y1 = Math.Min(mask.Y + mask.Height, region.Y + region.Height) - region.Y;
                if (x1 <= x0 || y1 <= y0) continue;
                var area = new RECT { left = x0, top = y0, right = x1, bottom = y1 };
                PInvoke.FillRect(dc, &area, black);
            }
        }
        finally
        {
            PInvoke.DeleteObject(black);
        }
    }

    private static unsafe CapturedImage? Capture(int width, int height, int originX, int originY, Func<HDC, bool> paint)
    {
        var screen = PInvoke.GetDC(HWND.Null);
        var memory = PInvoke.CreateCompatibleDC(screen);
        var bitmap = PInvoke.CreateCompatibleBitmap(screen, width, height);
        PInvoke.ReleaseDC(HWND.Null, screen);
        if (memory.IsNull || bitmap.IsNull)
        {
            if (!bitmap.IsNull) PInvoke.DeleteObject(bitmap);
            if (!memory.IsNull) PInvoke.DeleteDC(memory);
            return null;
        }

        var previous = PInvoke.SelectObject(memory, bitmap);
        try
        {
            if (!paint(memory)) return null;

            var stride = ((width * 3) + 3) & ~3;
            var pixels = new byte[stride * height];
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = width,
                    biHeight = height, // bottom-up, the plain BMP layout
                    biPlanes = 1,
                    biBitCount = 24,
                },
            };

            fixed (byte* p = pixels)
            {
                // The bitmap must not be selected into a DC while GetDIBits reads it.
                PInvoke.SelectObject(memory, previous);
                var lines = PInvoke.GetDIBits(memory, bitmap, 0, (uint)height, p, &info, DIB_USAGE.DIB_RGB_COLORS);
                if (lines == 0) return null;
            }

            return new CapturedImage(WrapAsBmp(pixels, width, height), width, height, originX, originY);
        }
        finally
        {
            PInvoke.DeleteObject(bitmap);
            PInvoke.DeleteDC(memory);
        }
    }

    private static byte[] WrapAsBmp(byte[] pixels, int width, int height)
    {
        const int headers = 14 + 40;
        var file = new byte[headers + pixels.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), headers);
        BitConverter.TryWriteBytes(file.AsSpan(14), 40);
        BitConverter.TryWriteBytes(file.AsSpan(18), width);
        BitConverter.TryWriteBytes(file.AsSpan(22), height);
        BitConverter.TryWriteBytes(file.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(file.AsSpan(28), (short)24);
        BitConverter.TryWriteBytes(file.AsSpan(34), pixels.Length);
        pixels.CopyTo(file.AsSpan(headers));
        return file;
    }
}
