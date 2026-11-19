using OmniWin.Core;

namespace OmniWin.Ocr;

/// <summary>
/// Reads the text in one window with Windows OCR for the terminal submission guard (<see cref="IWindowTextReader"/>). It uses
/// the same capture (PrintWindow, so a covering window is not read instead) and the same engine as <c>screen_ocr</c>, but it
/// registers no oN refs and returns text only. Any failure, a timeout or an empty result gives null, so the guard keeps its
/// block.
/// </summary>
public sealed class WindowTextReader : IWindowTextReader
{
    /// <summary>OCR must fit well inside the desktop dispatch budget (connection + 2 transactions, 4 s by default).</summary>
    internal const int DefaultTimeoutMs = 1500;

    private readonly IScreenCapture _capture;
    private readonly IOcrEngine _engine;
    private readonly int _timeoutMs;

    public WindowTextReader()
        : this(new GdiScreenCapture(), new WinRtOcrEngine())
    {
    }

    internal WindowTextReader(IScreenCapture capture, IOcrEngine engine, int timeoutMs = DefaultTimeoutMs)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _timeoutMs = timeoutMs > 0 ? timeoutMs : throw new ArgumentOutOfRangeException(nameof(timeoutMs));
    }

    public string? ReadWindowText(long hwnd)
    {
        if (hwnd == 0) return null;
        try
        {
            var image = _capture.CaptureWindow(hwnd);
            if (image is null || image.Width <= 0 || image.Height <= 0 ||
                image.Width > _engine.MaxImageDimension || image.Height > _engine.MaxImageDimension) return null;

            using var timeout = new CancellationTokenSource(_timeoutMs);
            // Off the caller's thread: the UI Automation worker has no synchronization context to resume on.
            var recognize = Task.Run(() => _engine.RecognizeAsync(image.Bmp, timeout.Token), timeout.Token);
            if (!recognize.Wait(_timeoutMs + 250)) return null;

            var text = string.Join('\n', recognize.Result.Select(line => line.Text));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }
}