using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace OmniWin.Ocr;

/// <summary>A word with its box in image pixels (top-left origin).</summary>
internal sealed record RecognizedWord(string Text, double X, double Y, double Width, double Height);

internal sealed record RecognizedLine(string Text, IReadOnlyList<RecognizedWord> Words);

internal sealed class OcrUnavailableException(string message) : Exception(message);

internal interface IOcrEngine
{
    /// <summary>The longest side of an image the engine accepts.</summary>
    int MaxImageDimension { get; }

    Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] bmp, CancellationToken ct);
}

/// <summary>The built-in Windows OCR (<c>Windows.Media.Ocr</c>), using the user's profile languages. Nothing is downloaded.</summary>
internal sealed class WinRtOcrEngine : IOcrEngine
{
    public int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    public async Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] bmp, CancellationToken ct)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new OcrUnavailableException("Windows OCR has no language available for this user profile.");

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bmp);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(ct).ConfigureAwait(false);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);

        var lines = new List<RecognizedLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Select(w => new RecognizedWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
                .ToList();
            lines.Add(new RecognizedLine(line.Text, words));
        }

        return lines;
    }
}
