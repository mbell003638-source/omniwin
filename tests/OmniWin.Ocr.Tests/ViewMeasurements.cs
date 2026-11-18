using System.Diagnostics;
using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.Ocr.Tests;

/// <summary>
/// screen_view timing (report-only row, target at most 300 ms for one 1920x1080 window), opt-in: set OMNIWIN_VIEW_BENCH=1.
/// It measures the mask + downscale + encode pipeline on an in-memory 1920x1080 image of text drawn with GDI, with one
/// password rectangle blacked out. The screen capture step is NOT measured: it needs an interactive desktop and this
/// session has none. Results go to artifacts/benchmarks/view-screen-v1.json.
/// </summary>
public sealed class ViewMeasurements
{
    public static bool Enabled => Environment.GetEnvironmentVariable("OMNIWIN_VIEW_BENCH") == "1";

    [Fact(Skip = "Set OMNIWIN_VIEW_BENCH=1 to measure.", SkipUnless = nameof(Enabled))]
    public async Task Measure_the_screen_view_pipeline_on_a_1080p_window_image()
    {
        var ct = TestContext.Current.CancellationToken;
        var lines = Enumerable.Range(1, 30).Select(i => $"Row {i}: The quick brown fox jumps over the lazy dog 0123456789").ToList();
        var bmp = RealOcrTests.DrawLines(lines, 1920, 1080, 40, 20, 24, 34);
        var masks = new List<PixelRect> { new(300, 200, 400, 40) };

        var first = await ViewImaging.RenderAsync(bmp, masks, null, ViewLimits.DefaultMaxSide, ct);
        for (var i = 0; i < 5; i++) await ViewImaging.RenderAsync(bmp, masks, null, ViewLimits.DefaultMaxSide, ct);

        var samples = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var sw = Stopwatch.StartNew();
            await ViewImaging.RenderAsync(bmp, masks, null, ViewLimits.DefaultMaxSide, ct);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        var sorted = samples.Order().ToList();
        double At(double q) => Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))], 2);
        var doc = new
        {
            started_utc = DateTimeOffset.UtcNow,
            note = "Mask + downscale + encode only. Screen capture is not measured: no interactive desktop in this session.",
            image = "1920x1080 24-bit, 30 lines of text drawn with GDI, one 400x40 password rectangle masked",
            default_max_side = ViewLimits.DefaultMaxSide,
            output_width = first.Width,
            output_height = first.Height,
            output_type = first.MediaType,
            output_kb = Math.Round(first.Data.Length / 1024.0, 1),
            samples = sorted.Count,
            p50_ms = At(0.5),
            p95_ms = At(0.95),
            p99_ms = At(0.99),
            max_ms = Math.Round(sorted[^1], 2),
        };
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OmniWin.sln"))) dir = Directory.GetParent(dir)?.FullName;
        var path = Path.Combine(dir!, "artifacts", "benchmarks", "view-screen-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, CoreJson.Indented), ct);
    }
}
