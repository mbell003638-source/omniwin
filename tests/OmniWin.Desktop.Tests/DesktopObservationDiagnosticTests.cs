using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

[Collection("Desktop input")]
[Trait("Category", "DesktopReadOnlyDiagnostic")]
public sealed class DesktopObservationDiagnosticTests
{
    private const int ColdCount = 20;
    private const int WarmCount = 200;

    public static bool DiagnosticsEnabled =>
        Environment.GetEnvironmentVariable("OMNIWIN_OBSERVE_DIAGNOSTICS") == "1";

    [Fact(Skip = "Set OMNIWIN_OBSERVE_DIAGNOSTICS=1 and the exact target process and window title.",
        SkipUnless = nameof(DiagnosticsEnabled))]
    public async Task Measure_forced_cold_and_normal_observations_of_the_exact_open_window()
    {
        var output = TestContext.Current.TestOutputHelper;
        var processName = Required("OMNIWIN_OBSERVE_PROCESS");
        var windowTitle = Required("OMNIWIN_OBSERVE_TITLE");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        using var currentProcess = Process.GetCurrentProcess();
        var started = DateTimeOffset.UtcNow;
        var config = new OmniWinConfig();
        var samples = new List<Sample>(ColdCount + WarmCount);
        WindowInfo? target = null;
        string? failure = null;
        await using var events = new UiaEvents(config.Timeouts);
        UiaDesktop? desktop = null;
        await using var worker = new UiaWorker("uia-observation-diagnostic", config.Timeouts, _ => desktop?.Dispose());

        async Task Read(string operation, int iteration, bool fresh)
        {
            var start = Stopwatch.GetTimestamp();
            var observation = await worker.InvokeAsync((uia, token) =>
            {
                desktop ??= new UiaDesktop(uia, events, config, null);
                return desktop.Observe(new DesktopObserveRequest(target!.Id, MaxElements: 1000, IncludeText: true), token, fresh);
            }, ct);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            samples.Add(new Sample(operation, iteration, elapsed, observation.Timing.TotalMs,
                observation.ElementCount, observation.OmittedCount));
            Assert.Equal(target!.Id, observation.Target);
            Assert.True(observation.ElementCount > 0, "The exact target did not expose visible controls.");
        }

        try
        {
            Assert.True(NativeDesktop.OnInputDesktop(), "The read-only diagnostic requires an active input desktop.");
            // Discovery and worker startup are outside the cold-observation measurements.
            var windows = await worker.InvokeAsync((uia, token) =>
            {
                desktop ??= new UiaDesktop(uia, events, config, null);
                return desktop.ListWindows(token);
            }, ct);
            var matches = windows.Where(window =>
                window.Process.Equals(processName, StringComparison.OrdinalIgnoreCase) &&
                window.Title.Equals(windowTitle, StringComparison.Ordinal)).ToArray();
            Assert.True(matches.Length == 1, $"The exact process and title matched {matches.Length} windows.");
            target = matches[0];
            Assert.Null(target.Inaccessible);
            Assert.False(target.IsMinimized);
            for (var i = 0; i < ColdCount; i++)
            {
                await Read("forced_cold", i, fresh: true);
            }

            for (var i = 0; i < WarmCount; i++)
            {
                await Read("normal_warm", i, fresh: false);
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
                .ToDictionary(group => group.Key, group => new OperationSummary(group.Count(),
                    Summarize(group.Select(sample => sample.FullCallMs)),
                    Summarize(group.Select(sample => sample.ReadAndRenderMs)),
                    group.Min(sample => sample.ElementCount), group.Max(sample => sample.ElementCount),
                    group.Min(sample => sample.OmittedCount), group.Max(sample => sample.OmittedCount)), StringComparer.Ordinal);
            var report = new
            {
                StartedAt = started,
                CompletedAt = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = Environment.OSVersion.VersionString,
                SessionId = currentProcess.SessionId,
                EventsRegistered = events.Available,
                Target = new { Process = processName, Title = windowTitle, Pid = target?.Pid, Hwnd = target?.Hwnd, WindowId = target?.Id },
                MaxElements = 1000,
                IncludeText = true,
                ForcedColdCount = ColdCount,
                NormalWarmCount = WarmCount,
                DesktopSha256 = Hash(typeof(DesktopEngine).Assembly.Location),
                HarnessSha256 = Hash(typeof(DesktopObservationDiagnosticTests).Assembly.Location),
                Failure = failure,
                Summaries = summaries,
                Samples = samples,
            };
            var path = Path.GetFullPath(Environment.GetEnvironmentVariable("OMNIWIN_OBSERVE_OUTPUT") ??
                Path.Combine(AppContext.BaseDirectory, "TestResults", "desktop-observation-diagnostic.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, CoreJson.Indented),
                TestContext.Current.CancellationToken);
            foreach (var (operation, summary) in summaries)
            {
                output?.WriteLine($"{operation} fullcall p50={summary.FullCallMs.P50:F3}ms " +
                    $"p95={summary.FullCallMs.P95:F3}ms read/render p50={summary.ReadAndRenderMs.P50:F3}ms " +
                    $"p95={summary.ReadAndRenderMs.P95:F3}ms n={summary.Count} " +
                    $"elements={summary.MinElements}..{summary.MaxElements} omitted={summary.MinOmitted}..{summary.MaxOmitted}");
            }

            output?.WriteLine($"Observation diagnostic report {path}");
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"{name} must identify the exact already-open target.");

    private static TimingSummary Summarize(IEnumerable<double> durations)
    {
        var values = durations.Order().ToArray();
        double Percentile(double p) => values[Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1)];
        return new TimingSummary(Percentile(0.5), Percentile(0.95), Percentile(0.99), values[^1]);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record TimingSummary(double P50, double P95, double P99, double Max);
    private sealed record OperationSummary(int Count, TimingSummary FullCallMs, TimingSummary ReadAndRenderMs,
        int MinElements, int MaxElements, int MinOmitted, int MaxOmitted);
    private sealed record Sample(string Operation, int Iteration, double FullCallMs, double ReadAndRenderMs,
        int ElementCount, int OmittedCount);
}
