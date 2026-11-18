using System.Runtime.InteropServices;
using OmniWin.Core;
using Windows.Win32.Graphics.Gdi;

namespace OmniWin.Ocr.Tests;

/// <summary>
/// GDI capture and masking. The screen-pixel tests need an interactive desktop (they skip on a locked or disconnected
/// session); the masking test draws on an in-memory bitmap and always runs. Pixels are inspected in memory only and are
/// never saved, logged or shown.
/// </summary>
public sealed class RealCaptureTests
{
    private const int HeaderBytes = 54;

    private static void RequireInteractiveDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x1 /* DESKTOP_READOBJECTS */);
        if (desktop == IntPtr.Zero)
        {
            Assert.Skip("No interactive desktop in this session (locked or disconnected): screen pixels can't be captured.");
        }

        CloseDesktop(desktop);
    }

    [Fact]
    public void A_region_capture_is_a_24_bit_bmp_of_the_requested_size_at_the_requested_origin()
    {
        RequireInteractiveDesktop();
        var capture = new GdiScreenCapture();
        var origin = capture.VirtualScreen;

        var image = capture.CaptureRegion(new ScreenRect(origin.X, origin.Y, 50, 40), []);

        Assert.NotNull(image);
        Assert.Equal(50, image.Width);
        Assert.Equal(40, image.Height);
        Assert.Equal(origin.X, image.OriginX);
        Assert.Equal(origin.Y, image.OriginY);
        Assert.Equal((byte)'B', image.Bmp[0]);
        Assert.Equal((byte)'M', image.Bmp[1]);
        Assert.Equal(HeaderBytes + (((50 * 3) + 3) & ~3) * 40, image.Bmp.Length);
        Assert.Equal(50, BitConverter.ToInt32(image.Bmp, 18));
        Assert.Equal(40, BitConverter.ToInt32(image.Bmp, 22));
        Assert.Equal(24, BitConverter.ToInt16(image.Bmp, 28));
    }

    [Fact]
    public void A_screen_region_covered_by_a_mask_comes_back_black()
    {
        RequireInteractiveDesktop();
        var capture = new GdiScreenCapture();
        var origin = capture.VirtualScreen;

        var masked = capture.CaptureRegion(new ScreenRect(origin.X, origin.Y, 64, 48),
            [new ScreenRect(origin.X - 20, origin.Y - 20, 200, 200)]);

        Assert.NotNull(masked);
        Assert.All(masked.Bmp.Skip(HeaderBytes), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Masks_paint_black_only_inside_the_mask_and_clip_to_the_region()
    {
        const int width = 64;
        const int height = 48;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        ReleaseDC(IntPtr.Zero, screen);
        var previous = SelectObject(dc, bitmap);
        try
        {
            // White bitmap whose top-left is screen (100,200).
            var white = CreateSolidBrush(0xFFFFFF);
            var all = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
            FillRect(dc, ref all, white);
            DeleteObject(white);

            // One mask overlaps the left part and sticks out above and left of the region; one lies wholly outside.
            GdiScreenCapture.PaintMasks(new HDC(dc), new ScreenRect(100, 200, width, height),
                [new ScreenRect(80, 190, 52, 40), new ScreenRect(5000, 5000, 10, 10)]);

            SelectObject(dc, previous);
            var stride = ((width * 3) + 3) & ~3;
            var pixels = new byte[stride * height];
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = height, Planes = 1, BitCount = 24 };
            Assert.NotEqual(0, GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0));

            // The mask covers x 0..31 and y 0..29 of the bitmap (80+52-100 = 32, 190+40-200 = 30).
            // BMP rows run bottom-up, so image row y is stored at index (height - 1 - y).
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = ((height - 1 - y) * stride) + (x * 3);
                    var black = pixels[offset] == 0 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0;
                    var inMask = x < 32 && y < 30;
                    Assert.True(black == inMask, $"pixel {x},{y}: black={black}, expected {inMask}");
                }
            }
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [Fact]
    public void A_degenerate_region_gives_no_image()
    {
        Assert.Null(new GdiScreenCapture().CaptureRegion(new ScreenRect(0, 0, 0, 10), []));
    }

    [Fact]
    public void A_window_that_does_not_exist_has_no_rect_and_cannot_be_captured()
    {
        var capture = new GdiScreenCapture();

        Assert.Null(capture.WindowRect(0x7FFFFFFE));
        Assert.Null(capture.CaptureWindow(0x7FFFFFFE));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
