namespace OmniWin.Core;

/// <summary>
/// Draws element-ref labels (an outline around the element and its ref in a small yellow chip) on 32-bit BGRA pixels, with a
/// built-in 5x7 pixel font for the characters of a ref ("e" and digits). No fonts, no packages, no files.
/// </summary>
internal static class LabelPainter
{
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;
    private const int Scale = 2;
    private const int MaxLabels = 250;

    // Rows of 5 bits, top to bottom.
    private static readonly Dictionary<char, byte[]> Font = new()
    {
        ['0'] = [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        ['1'] = [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        ['2'] = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
        ['3'] = [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
        ['4'] = [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        ['5'] = [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        ['6'] = [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        ['7'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        ['8'] = [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        ['9'] = [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        ['e'] = [0b00000, 0b00000, 0b01110, 0b10001, 0b11111, 0b10000, 0b01110],
    };

    internal static void Draw(byte[] pixels, int width, int height, IReadOnlyList<LabelBox> labels, IReadOnlyList<PixelRect> masks)
    {
        var drawn = 0;
        foreach (var label in labels)
        {
            if (drawn >= MaxLabels) break;
            var box = label.Box;
            if (box.Width < 4 || box.Height < 4) continue;
            if (box.X >= width || box.Y >= height || box.X + box.Width <= 0 || box.Y + box.Height <= 0) continue;
            if (masks.Any(m => Intersects(m, box))) continue;

            Outline(pixels, width, height, box, b: 255, g: 0, r: 255);   // magenta
            Chip(pixels, width, height, box.X, box.Y, label.Text);
            drawn++;
        }
    }

    private static bool Intersects(PixelRect a, PixelRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static void Outline(byte[] pixels, int width, int height, PixelRect box, byte b, byte g, byte r)
    {
        var x0 = box.X;
        var y0 = box.Y;
        var x1 = box.X + box.Width - 1;
        var y1 = box.Y + box.Height - 1;
        for (var x = x0; x <= x1; x++)
        {
            Put(pixels, width, height, x, y0, b, g, r);
            Put(pixels, width, height, x, y1, b, g, r);
        }

        for (var y = y0; y <= y1; y++)
        {
            Put(pixels, width, height, x0, y, b, g, r);
            Put(pixels, width, height, x1, y, b, g, r);
        }
    }

    private static void Chip(byte[] pixels, int width, int height, int left, int top, string text)
    {
        var glyphs = text.Where(Font.ContainsKey).ToArray();
        if (glyphs.Length == 0) return;
        var chipW = glyphs.Length * (GlyphWidth + 1) * Scale + Scale + 1;
        var chipH = GlyphHeight * Scale + Scale * 2;
        // Yellow chip with black text; kept inside the picture.
        var x0 = Math.Clamp(left, 0, Math.Max(0, width - chipW));
        var y0 = Math.Clamp(top, 0, Math.Max(0, height - chipH));
        for (var y = 0; y < chipH; y++)
        {
            for (var x = 0; x < chipW; x++)
            {
                Put(pixels, width, height, x0 + x, y0 + y, 0, 255, 255);   // yellow (BGR)
            }
        }

        var cursor = x0 + Scale;
        foreach (var ch in glyphs)
        {
            var rows = Font[ch];
            for (var gy = 0; gy < GlyphHeight; gy++)
            {
                for (var gx = 0; gx < GlyphWidth; gx++)
                {
                    if ((rows[gy] & (1 << (GlyphWidth - 1 - gx))) == 0) continue;
                    for (var sy = 0; sy < Scale; sy++)
                    {
                        for (var sx = 0; sx < Scale; sx++)
                        {
                            Put(pixels, width, height, cursor + gx * Scale + sx, y0 + Scale + gy * Scale + sy, 0, 0, 0);
                        }
                    }
                }
            }

            cursor += (GlyphWidth + 1) * Scale;
        }
    }

    private static void Put(byte[] pixels, int width, int height, int x, int y, byte b, byte g, byte r)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return;
        var o = ((y * width) + x) * 4;
        pixels[o] = b;
        pixels[o + 1] = g;
        pixels[o + 2] = r;
        pixels[o + 3] = 255;
    }
}
