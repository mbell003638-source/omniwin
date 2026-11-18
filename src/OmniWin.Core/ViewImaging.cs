using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OmniWin.Core;

/// <summary>A rectangle in image pixels (top-left origin).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>A text label (an element ref such as "e12") with the box of its element, in image pixels.</summary>
public readonly record struct LabelBox(string Text, PixelRect Box);

/// <summary>
/// Masks, crops, downscales and encodes a captured image with the built-in Windows imaging (no packages, no files).
/// Masks are painted black on the full-resolution pixels BEFORE any scaling or encoding, so no sensitive pixel survives
/// into the returned bytes. Pixels stay in memory.
/// </summary>
public static class ViewImaging
{
    private const float JpegQuality = 0.85f;

    /// <summary>Decodes a BMP, PNG or JPEG into 32-bit BGRA pixels (rows top to bottom).</summary>
    public static async Task<(int Width, int Height, byte[] Bgra)> DecodeAsync(byte[] image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(image);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
        return ((int)decoder.PixelWidth, (int)decoder.PixelHeight, data.DetachPixelData());
    }

    /// <summary>
    /// Applies <paramref name="crop"/> (if any), paints every mask rectangle black (rectangles are in the coordinates of the
    /// original image and are clipped to it), downscales so the longest side is at most <paramref name="maxSide"/>, and
    /// returns whichever of PNG and JPEG is smaller.
    /// </summary>
    public static async Task<ViewImage> RenderAsync(
        byte[] image, IReadOnlyList<PixelRect> masks, PixelRect? crop, int maxSide, CancellationToken ct,
        IReadOnlyList<LabelBox>? labels = null)
    {
        ArgumentNullException.ThrowIfNull(masks);
        var (srcW, srcH, pixels) = await DecodeAsync(image, ct).ConfigureAwait(false);

        // Black out first, on the full-resolution pixels.
        foreach (var mask in masks)
        {
            Paint(pixels, srcW, srcH, mask);
        }

        // Labels go on after the masks, on the same full-resolution pixels. A label whose box touches a mask is left out, so
        // nothing is drawn on or next to a blacked-out area.
        if (labels is { Count: > 0 })
        {
            LabelPainter.Draw(pixels, srcW, srcH, labels, masks);
        }

        var width = srcW;
        var height = srcH;
        if (crop is { } c)
        {
            var x0 = Math.Clamp(c.X, 0, srcW);
            var y0 = Math.Clamp(c.Y, 0, srcH);
            var x1 = Math.Clamp(c.X + c.Width, 0, srcW);
            var y1 = Math.Clamp(c.Y + c.Height, 0, srcH);
            if (x1 <= x0 || y1 <= y0) throw new InvalidOperationException("The crop rectangle is outside the image.");
            width = x1 - x0;
            height = y1 - y0;
            var cropped = new byte[width * height * 4];
            for (var row = 0; row < height; row++)
            {
                System.Buffer.BlockCopy(pixels, (((y0 + row) * srcW) + x0) * 4, cropped, row * width * 4, width * 4);
            }

            pixels = cropped;
        }

        var scale = Math.Min(1.0, maxSide / (double)Math.Max(width, height));
        var outW = Math.Max(1, (int)Math.Round(width * scale));
        var outH = Math.Max(1, (int)Math.Round(height * scale));

        using var bitmap = CreateBitmap(pixels, width, height);
        var png = await EncodeAsync(BitmapEncoder.PngEncoderId, bitmap, outW, outH, width, height, null, ct).ConfigureAwait(false);
        var jpeg = await EncodeAsync(BitmapEncoder.JpegEncoderId, bitmap, outW, outH, width, height, JpegQuality, ct).ConfigureAwait(false);

        var usePng = png.Length <= jpeg.Length;
        return new ViewImage(usePng ? png : jpeg, usePng ? "image/png" : "image/jpeg", outW, outH, width, height, scale);
    }

    private static void Paint(byte[] pixels, int width, int height, PixelRect mask)
    {
        var x0 = Math.Clamp(mask.X, 0, width);
        var y0 = Math.Clamp(mask.Y, 0, height);
        var x1 = Math.Clamp(mask.X + mask.Width, 0, width);
        var y1 = Math.Clamp(mask.Y + mask.Height, 0, height);
        for (var y = y0; y < y1; y++)
        {
            var offset = ((y * width) + x0) * 4;
            for (var x = x0; x < x1; x++, offset += 4)
            {
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                pixels[offset + 3] = 255;
            }
        }
    }

    private static SoftwareBitmap CreateBitmap(byte[] pixels, int width, int height)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        return SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    }

    private static async Task<byte[]> EncodeAsync(
        Guid encoderId, SoftwareBitmap bitmap, int outW, int outH, int srcW, int srcH, float? quality, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet();
        if (quality is { } q)
        {
            options.Add("ImageQuality", new BitmapTypedValue(q, Windows.Foundation.PropertyType.Single));
        }

        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream, options).AsTask(ct).ConfigureAwait(false);
        encoder.SetSoftwareBitmap(bitmap);
        if (outW != srcW || outH != srcH)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)outW;
            encoder.BitmapTransform.ScaledHeight = (uint)outH;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)bytes.Length).AsTask(ct).ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
