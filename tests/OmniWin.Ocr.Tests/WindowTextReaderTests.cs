namespace OmniWin.Ocr.Tests;

public sealed class WindowTextReaderTests
{
    [Fact]
    public void Returns_the_recognised_lines_of_the_window()
    {
        var capture = new FakeCapture();
        var reader = new WindowTextReader(capture, new FakeEngine());
        Assert.Equal("Save As\nCancel", reader.ReadWindowText(42));
        Assert.Equal(1, capture.WindowCaptures);
    }

    [Fact]
    public void No_window_no_capture_or_no_text_gives_null()
    {
        Assert.Null(new WindowTextReader(new FakeCapture(), new FakeEngine()).ReadWindowText(0));
        Assert.Null(new WindowTextReader(new FakeCapture { Image = null }, new FakeEngine()).ReadWindowText(42));
        Assert.Null(new WindowTextReader(new FakeCapture(), new FakeEngine { Lines = [] }).ReadWindowText(42));
        Assert.Null(new WindowTextReader(new FakeCapture(), new FakeEngine { Lines = [new("  ", [])] }).ReadWindowText(42));
    }

    [Fact]
    public void Engine_failure_oversized_capture_and_timeout_give_null()
    {
        Assert.Null(new WindowTextReader(new FakeCapture(), new FakeEngine { Throw = new InvalidOperationException("boom") }).ReadWindowText(42));
        Assert.Null(new WindowTextReader(new FakeCapture(), new FakeEngine { MaxImageDimension = 50 }).ReadWindowText(42));
        var slow = new FakeEngine { Hook = ct => Task.Delay(Timeout.Infinite, ct) };
        Assert.Null(new WindowTextReader(new FakeCapture(), slow, timeoutMs: 50).ReadWindowText(42));
    }
}