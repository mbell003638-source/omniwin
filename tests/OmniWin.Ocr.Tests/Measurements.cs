using System.Diagnostics;
using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.Ocr.Tests;

/// <summary>
/// OCR timing (contract §15), opt-in: set OMNIWIN_WS3_BENCH=1. Measures Windows OCR itself on an in-memory 1920x1080 image
/// of about 30 lines of text, drawn with GDI. The screen capture step is NOT measured: it needs an interactive desktop and
/// this session has none (see docs/status/WS3.md). Results go to artifacts/benchmarks/ws3-ocr-v1.json.
/// </summary>
public sealed class Measurements
{
    public static bool Enabled => Environment.GetEnvironmentVariable("OMNIWIN_WS3_BENCH") == "1";

    [Fact(Skip = "Set OMNIWIN_WS3_BENCH=1 to measure.", SkipUnless = nameof(Enabled))]
    public async Task Measure_windows_ocr_on_a_1080p_image()
    {
        var ct = TestContext.Current.CancellationToken;
        var lines = Enumerable.Range(1, 30).Select(i => $"Row {i}: The quick brown fox jumps over the lazy dog 0123456789").ToList();
        var bmp = RealOcrTests.DrawLines(lines, 1920, 1080, 40, 20, 24, 34);
        var engine = new WinRtOcrEngine();

        IReadOnlyList<RecognizedLine> first;
        try
        {
            var swFirst = Stopwatch.StartNew();
            first = await engine.RecognizeAsync(bmp, ct);
            swFirst.Stop();
            var words = first.Sum(l => l.Words.Count);

            for (var i = 0; i < 5; i++) await engine.RecognizeAsync(bmp, ct);
            var samples = new List<double>();
            for (var i = 0; i < 50; i++)
            {
                var sw = Stopwatch.StartNew();
                await engine.RecognizeAsync(bmp, ct);
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            var sorted = samples.Order().ToList();
            double At(double q) => Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))], 2);
            var doc = new
            {
                started_utc = DateTimeOffset.UtcNow,
                note = "Windows OCR only (capture not measured: no interactive desktop in this session).",
                image = "1920x1080 24-bit, 30 lines of text drawn with GDI",
                words_found = words,
                first_call_ms = Math.Round(swFirst.Elapsed.TotalMilliseconds, 2),
                samples = sorted.Count,
                p50_ms = At(0.5),
                p95_ms = At(0.95),
                p99_ms = At(0.99),
                max_ms = Math.Round(sorted[^1], 2),
            };
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "OmniWin.sln"))) dir = Directory.GetParent(dir)?.FullName;
            var path = Path.Combine(dir!, "artifacts", "benchmarks", "ws3-ocr-v1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, CoreJson.Indented), ct);
        }
        catch (OcrUnavailableException ex)
        {
            Assert.Skip(ex.Message);
        }
    }
}
