using System.Runtime.InteropServices;
using OmniWin.Core;

namespace OmniWin.Ocr.Tests;

public sealed class OcrFormatTests
{
    private static OcrCapture Sample(params string[] wordTexts)
    {
        var words = wordTexts.Select((t, i) => new OcrWord(t, new ScreenRect(i * 50, 0, 40, 14), "o" + (i + 1))).ToList();
        var lines = new List<OcrLineResult> { new(string.Join(' ', wordTexts), words) };
        return new OcrCapture(new OcrResult(string.Join(' ', wordTexts), words, Timing.Total(1)), lines, "region 1,2 3x4");
    }

    [Fact]
    public void Output_starts_with_the_untrusted_header_then_a_summary_line_then_the_words_with_ids()
    {
        var text = OcrFormat.Render(Sample("Save", "As"), new Timing(12.3, new Dictionary<string, double> { ["read"] = 10.1 }));

        var lines = text.Split('\n');
        Assert.Equal(ResultFormatter.UntrustedHeader, lines[0]);
        Assert.StartsWith("ocr target=region_1,2_3x4 words=2 lines=1 total=", lines[1]);
        Assert.Equal("o1 \"Save\" o2 \"As\"", lines[2]);
    }

    [Fact]
    public void Word_text_from_the_screen_cannot_add_lines_or_break_out_of_its_quotes()
    {
        var text = OcrFormat.Render(Sample("a\nb", "he said \"hi\""), Timing.Total(1));

        // Header, summary and exactly one word line: the embedded newline did not create a line.
        Assert.Equal(3, text.Split('\n').Length);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void Output_holds_text_only_no_image_data()
    {
        var text = OcrFormat.Render(Sample("Save"), Timing.Total(1));

        Assert.DoesNotContain("BM", text.Split('\n')[2]);
        Assert.All(text, c => Assert.True(c == '\n' || c >= ' ', "control character in OCR output"));
        Assert.True(text.Length < 400);
    }

    [Fact]
    public void Words_without_an_id_are_listed_without_one()
    {
        var capture = Sample("Hello");
        var noId = capture with { Lines = [new OcrLineResult("Hello", [new OcrWord("Hello", new ScreenRect(0, 0, 1, 1), null)])] };

        Assert.Equal("\"Hello\"", OcrFormat.Render(noId, Timing.Total(1)).Split('\n')[2]);
    }
}

/// <summary>
/// Real Windows OCR on an image the test draws itself with GDI in memory (no window is shown, nothing is captured from the
/// screen). If this PC has no OCR language the test says so and stops instead of failing.
/// </summary>
public sealed class RealOcrTests
{
    [Fact]
    public async Task Real_windows_ocr_finds_drawn_words_and_their_boxes()
    {
        var bmp = DrawText("OmniWin Test 12345", 600, 120, 20, 30, 40);

        IReadOnlyList<RecognizedLine> lines;
        try
        {
            lines = await new WinRtOcrEngine().RecognizeAsync(bmp, TestContext.Current.CancellationToken);
        }
        catch (OcrUnavailableException ex)
        {
            Assert.Skip(ex.Message);
            return;
        }

        var words = lines.SelectMany(l => l.Words).ToList();
        var joined = string.Join(' ', words.Select(w => w.Text));
        Assert.Contains("OmniWin", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12345", joined);

        // Boxes land on the drawn text (drawn at x=20,y=30, 40 px tall, inside a 600x120 image).
        var first = words.First(w => w.Text.Contains("Omni", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(first.X, 10, 60);
        Assert.InRange(first.Y, 15, 60);
        Assert.InRange(first.Height, 15, 70);
        Assert.All(words, w => Assert.InRange(w.X + w.Width, 0, 600));
    }

    [Fact]
    public async Task Real_windows_ocr_on_a_blank_image_returns_no_words()
    {
        var bmp = DrawText(string.Empty, 300, 100, 0, 0, 20);

        IReadOnlyList<RecognizedLine> lines;
        try
        {
            lines = await new WinRtOcrEngine().RecognizeAsync(bmp, TestContext.Current.CancellationToken);
        }
        catch (OcrUnavailableException ex)
        {
            Assert.Skip(ex.Message);
            return;
        }

        Assert.Empty(lines.SelectMany(l => l.Words));
    }

    [Fact]
    public void Real_engine_reports_a_positive_image_limit()
    {
        Assert.True(new WinRtOcrEngine().MaxImageDimension > 0);
    }

    // ---- a tiny in-memory GDI renderer: black text on white, returned as a 24-bit BMP file ----

    internal static byte[] DrawText(string text, int width, int height, int x, int y, int fontHeight) =>
        DrawLines(text.Length == 0 ? [] : [text], width, height, x, y, fontHeight, fontHeight + 8);

    internal static byte[] DrawLines(IReadOnlyList<string> lines, int width, int height, int x, int y, int fontHeight, int lineStep)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        ReleaseDC(IntPtr.Zero, screen);
        var previous = SelectObject(dc, bitmap);
        try
        {
            var white = CreateSolidBrush(0xFFFFFF);
            var rect = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
            FillRect(dc, ref rect, white);
            DeleteObject(white);

            if (lines.Count > 0)
            {
                var font = CreateFontW(fontHeight, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Arial");
                var oldFont = SelectObject(dc, font);
                SetBkMode(dc, 1 /* TRANSPARENT */);
                SetTextColor(dc, 0);
                for (var i = 0; i < lines.Count; i++)
                {
                    TextOutW(dc, x, y + (i * lineStep), lines[i], lines[i].Length);
                }

                SelectObject(dc, oldFont);
                DeleteObject(font);
            }

            SelectObject(dc, previous);
            var stride = ((width * 3) + 3) & ~3;
            var pixels = new byte[stride * height];
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = height, Planes = 1, BitCount = 24 };
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) == 0) throw new InvalidOperationException("GetDIBits failed");

            var file = new byte[54 + pixels.Length];
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
            BitConverter.TryWriteBytes(file.AsSpan(10), 54);
            BitConverter.TryWriteBytes(file.AsSpan(14), 40);
            BitConverter.TryWriteBytes(file.AsSpan(18), width);
            BitConverter.TryWriteBytes(file.AsSpan(22), height);
            BitConverter.TryWriteBytes(file.AsSpan(26), (short)1);
            BitConverter.TryWriteBytes(file.AsSpan(28), (short)24);
            BitConverter.TryWriteBytes(file.AsSpan(34), pixels.Length);
            pixels.CopyTo(file.AsSpan(54));
            return file;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
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

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr dc, int x, int y, string text, int length);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
