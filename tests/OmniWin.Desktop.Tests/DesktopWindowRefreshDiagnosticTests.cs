using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

[Collection("Desktop input")]
[Trait("Category", "DesktopReadOnlyDiagnostic")]
public sealed class DesktopWindowRefreshDiagnosticTests
{
    private const int MeasuredCount = 200;
    private const int WarmupCount = 10;

    public static bool DiagnosticsEnabled =>
        Environment.GetEnvironmentVariable("OMNIWIN_WINDOW_REFRESH_DIAGNOSTICS") == "1";

    [Fact(Skip = "Set OMNIWIN_WINDOW_REFRESH_DIAGNOSTICS=1 on an interactive desktop.",
        SkipUnless = nameof(DiagnosticsEnabled))]
    public async Task Measure_window_listing_with_provider_events_unavailable()
    {
        var output = TestContext.Current.TestOutputHelper;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        var started = DateTimeOffset.UtcNow;
        var config = new OmniWinConfig();
        using var currentProcess = Process.GetCurrentProcess();
        var samples = new List<Sample>(MeasuredCount * 2 + 1);
        string? failure = null;
        await using var events = new UiaEvents(config.Timeouts, registerEvents: false);
        UiaDesktop? desktop = null;
        await using var worker = new UiaWorker("uia-window-refresh-diagnostic", config.Timeouts, _ => desktop?.Dispose());

        async Task Read(string operation, int iteration, bool record = true)
        {
            var capture = new DesktopDiagnostics.Capture();
            var beforeForeground = NativeDesktop.Root(NativeDesktop.Foreground);
            var start = Stopwatch.GetTimestamp();
            var windows = await worker.InvokeAsync((uia, token) =>
            {
                using var diagnostic = DesktopDiagnostics.Activate(capture, operation);
                desktop ??= new UiaDesktop(uia, events, config, null);
                return desktop.ListWindows(token);
            }, ct);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var afterForeground = NativeDesktop.Root(NativeDesktop.Foreground);
            if (record)
            {
                var diagnostics = new Dictionary<string, DiagnosticTiming>(capture.Snapshot());
                var stableForeground = beforeForeground == afterForeground &&
                    afterForeground != 0 && NativeDesktop.IsTopLevelWindow(afterForeground);
                samples.Add(new Sample(operation, iteration, elapsed, windows.Count,
                    stableForeground, !stableForeground || windows.Any(window => window.Hwnd == afterForeground),
                    diagnostics.GetValueOrDefault(operation + ".FullWindowUiaScan").Calls, diagnostics));
            }
        }

        try
        {
            Assert.False(events.Available);
            Assert.True(NativeDesktop.OnInputDesktop(), "The read-only diagnostic requires an active input desktop.");
            await Read("initial", 0);
            Assert.True(samples[0].WindowCount > 0, "The diagnostic did not discover any visible desktop windows.");
            for (var i = 0; i < WarmupCount; i++)
            {
                await Read("burst", i, record: false);
            }

            for (var i = 0; i < MeasuredCount; i++)
            {
                await Read("burst", i);
            }

            for (var i = 0; i < MeasuredCount; i++)
            {
                // The fallback TTL is 25 ms. This bounded measurement delay forces that path,
                // and is excluded from the reported window-listing duration.
                await Task.Delay(35, ct);
                await Read("fallback", i);
            }
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name} (0x{ex.HResult:X8})";
            throw;
        }
        finally
        {
            var summaries = samples.GroupBy(sample => sample.Operation, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Summarize(group.Select(sample => sample.TotalMs)), StringComparer.Ordinal);
            var report = new
            {
                StartedAt = started,
                CompletedAt = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = Environment.OSVersion.VersionString,
                SessionId = currentProcess.SessionId,
                EventsRegistered = events.Available,
                MeasuredCountPerOperation = MeasuredCount,
                FallbackDelayMs = 35,
                DesktopSha256 = Hash(typeof(DesktopEngine).Assembly.Location),
                HarnessSha256 = Hash(typeof(DesktopWindowRefreshDiagnosticTests).Assembly.Location),
                Failure = failure,
                StableForegroundSamplesMissingFromList = samples.Count(sample =>
                    sample.StableForeground && !sample.ForegroundPresent),
                FullWindowUiaScanCalls = samples.Sum(sample => sample.FullWindowUiaScanCalls),
                FallbackFullWindowUiaScanCalls = samples.Where(sample => sample.Operation == "fallback")
                    .Sum(sample => sample.FullWindowUiaScanCalls),
                Summaries = summaries,
                Samples = samples,
            };
            var path = Path.GetFullPath(Environment.GetEnvironmentVariable("OMNIWIN_WINDOW_REFRESH_OUTPUT") ??
                Path.Combine(AppContext.BaseDirectory, "TestResults", "desktop-window-refresh.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, CoreJson.Indented),
                TestContext.Current.CancellationToken);
            foreach (var (operation, summary) in summaries)
            {
                output?.WriteLine($"{operation} p50={summary.P50:F3}ms p95={summary.P95:F3}ms " +
                    $"p99={summary.P99:F3}ms n={summary.Count}");
            }

            output?.WriteLine($"Window refresh diagnostic report {path}");
        }
    }

    private static Summary Summarize(IEnumerable<double> durations)
    {
        var values = durations.Order().ToArray();
        double Percentile(double p) => values[Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1)];
        return new Summary(values.Length, Percentile(0.5), Percentile(0.95), Percentile(0.99), values[^1]);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record Summary(int Count, double P50, double P95, double P99, double Max);
    private sealed record Sample(string Operation, int Iteration, double TotalMs, int WindowCount,
        bool StableForeground, bool ForegroundPresent, int FullWindowUiaScanCalls,
        Dictionary<string, DiagnosticTiming> Diagnostics);
}
