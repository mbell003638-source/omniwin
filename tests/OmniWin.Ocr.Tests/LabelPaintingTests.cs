using OmniWin.Core;

namespace OmniWin.Ocr.Tests;

/// <summary>
/// screen_view labels=true: the refs are drawn on the full-resolution pixels AFTER the masks. A label whose element touches a
/// masked (password) area is left out entirely, so nothing is drawn on or next to a blacked-out area. In memory only.
/// </summary>
public sealed class LabelPaintingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Width, int Height, byte[] Bgra)> RenderAsync(IReadOnlyList<PixelRect> masks, IReadOnlyList<LabelBox>? labels)
    {
        var white = TestBmp.Solid(240, 140, 255, 255, 255);
        var rendered = await ViewImaging.RenderAsync(white, masks, null, 1920, Ct, labels);
        return await ViewImaging.DecodeAsync(rendered.Data, Ct);
    }

    private static (byte B, byte G, byte R) Pixel((int Width, int Height, byte[] Bgra) image, int x, int y)
    {
        var o = ((y * image.Width) + x) * 4;
        return (image.Bgra[o], image.Bgra[o + 1], image.Bgra[o + 2]);
    }

    [Fact]
    public async Task A_labelled_element_gets_a_magenta_outline()
    {
        var image = await RenderAsync([], [new LabelBox("e12", new PixelRect(100, 60, 80, 50))]);

        // The outline sits on the box edge: left edge at x=100, bottom edge at y=109.
        var edge = Pixel(image, 100, 95);
        Assert.True(edge.R > 180 && edge.B > 180 && edge.G < 100, $"outline pixel was {edge}");
        var bottom = Pixel(image, 140, 109);
        Assert.True(bottom.R > 180 && bottom.B > 180 && bottom.G < 100, $"bottom edge pixel was {bottom}");
    }

    [Fact]
    public async Task Without_labels_the_picture_is_unchanged()
    {
        var image = await RenderAsync([], null);

        var middle = Pixel(image, 100, 95);
        Assert.True(middle.R > 240 && middle.G > 240 && middle.B > 240);
    }

    [Fact]
    public async Task A_label_that_touches_a_masked_area_is_left_out_and_the_mask_stays_black()
    {
        var image = await RenderAsync([new PixelRect(10, 10, 60, 40)], [new LabelBox("e5", new PixelRect(40, 30, 50, 40))]);

        var inMask = Pixel(image, 20, 20);
        Assert.True(inMask.R < 60 && inMask.G < 60 && inMask.B < 60, $"masked pixel was {inMask}");
        // The left-out label's outline would have run along y=69; nothing of it is there.
        var wouldBeEdge = Pixel(image, 65, 69);
        Assert.True(wouldBeEdge.R > 200 && wouldBeEdge.G > 200 && wouldBeEdge.B > 200, $"the left-out label's edge pixel was {wouldBeEdge}");
    }

    [Fact]
    public async Task A_label_outside_the_picture_or_too_small_to_show_is_ignored_without_error()
    {
        var image = await RenderAsync([], [new LabelBox("e1", new PixelRect(1000, 1000, 50, 50)), new LabelBox("e2", new PixelRect(5, 5, 2, 2))]);

        Assert.Equal(240, image.Width);
        Assert.Equal(140, image.Height);
    }

    [Fact]
    public void The_view_text_names_the_label_count_and_the_refs_generation()
    {
        var image = new ViewImage([1, 2, 3], "image/png", 10, 10, 10, 10, 1);
        var plain = ViewFormat.Render(new ViewOutcome(image, null, "w3", "Title", DateTimeOffset.UtcNow, Timing.Total(5)));
        var labelled = ViewFormat.Render(new ViewOutcome(image, null, "w3", "Title", DateTimeOffset.UtcNow, Timing.Total(5), Labels: 12, LabelsGeneration: 7));

        Assert.DoesNotContain("labels=", plain, StringComparison.Ordinal);
        Assert.Contains("labels=12 refs_gen=7", labelled, StringComparison.Ordinal);
    }
}
