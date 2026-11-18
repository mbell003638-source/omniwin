using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

[Collection("Desktop input")]
[Trait("Category", "UiInputBenchmark")]
public sealed class DesktopBenchmarkTests
{
    public static bool BenchmarkEnabled => Environment.GetEnvironmentVariable("OMNIWIN_UI_BENCHMARK") == "1";

    [Fact(Skip = "Set OMNIWIN_UI_BENCHMARK=1 on an idle interactive desktop.", SkipUnless = nameof(BenchmarkEnabled))]
    public Task Measure_checked_clicks_typing_and_keys_on_owned_fixture()
    {
        var output = TestContext.Current.TestOutputHelper;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Run(line => output?.WriteLine(line));
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }) { IsBackground = true, Name = "omniwin-ui-benchmark" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Run(Action<string> output)
    {
        using var mutex = new Mutex(false, @"Local\OmniWin.UiInputTestLock");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { acquired = true; }
            Assert.True(acquired, "Another test holds the desktop input lock.");
            Measure(output);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void Measure(Action<string> output)
    {
        const int warmupCount = 10;
        const int measuredCount = 200;
        var textLength = Environment.GetEnvironmentVariable("OMNIWIN_BENCHMARK_TEXT_LENGTH") switch
        {
            null or "23" => 23,
            "100" => 100,
            _ => throw new InvalidOperationException("OMNIWIN_BENCHMARK_TEXT_LENGTH must be 23 or 100."),
        };
        var samples = new List<Sample>();
        var metrics = new RollingMetrics();
        var config = new OmniWinConfig { UserActivityCooldownMs = 500 };
        // Include the production low-level hooks in SendInput timings and retain the real stop key.
        using var safety = new KillSwitch(config, NullLogger<KillSwitch>.Instance);
        Assert.Equal("armed", safety.State);
        Assert.Equal("on", safety.ActivityStatus.State);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        var title = "OmniWin benchmark " + Guid.NewGuid().ToString("N");
        var launch = new ProcessStartInfo(FixturePath()) { UseShellExecute = false, RedirectStandardOutput = true };
        launch.ArgumentList.Add("--title");
        launch.ArgumentList.Add(title);
        using var fixture = Process.Start(launch) ?? throw new InvalidOperationException("Fixture launch failed.");
        DesktopEngine? engine = null;
        IReadOnlyDictionary<string, DiagnosticTiming>? diagnostic = null;
        string? failure = null;
        var started = DateTimeOffset.UtcNow;
        try
        {
            Assert.Equal("OMNIWIN_FIXTURE_READY", fixture.StandardOutput.ReadLineAsync(ct).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), ct).GetAwaiter().GetResult());
            engine = new DesktopEngine(config, new SafetyGate(config, safety), safety, metrics,
                NullLogger<DesktopEngine>.Instance, safety);
            if (Environment.GetEnvironmentVariable("OMNIWIN_BENCHMARK_PROFILE") == "1")
                engine.DiagnosticSink = values => diagnostic = values;
            WindowInfo? window = null;
            var discovery = Stopwatch.GetTimestamp();
            while (window is null && Stopwatch.GetElapsedTime(discovery) < TimeSpan.FromSeconds(15))
            {
                window = engine.ListWindowsAsync(ct).GetAwaiter().GetResult()
                    .FirstOrDefault(candidate => candidate.Pid == fixture.Id && candidate.Title == title);
                if (window is null) ct.WaitHandle.WaitOne(25);
            }
            Assert.NotNull(window);
            Assert.Null(window.Inaccessible);
            // Real input arriving later aborts the run instead of being silently excluded from results.
            while (safety.UserActiveRecently)
            {
                ct.ThrowIfCancellationRequested();
                ct.WaitHandle.WaitOne(50);
            }

            Observation Observe() => engine.ObserveAsync(new DesktopObserveRequest(window.Id, IncludeText: true), ct)
                .GetAwaiter().GetResult();
            var initial = Observe();
            if (NativeDesktop.Foreground != window.Hwnd)
            {
                var activate = engine.ActAsync(new DesktopActRequest(Ref(initial, "edit", "Editor"), initial.Generation, "click"), ct)
                    .GetAwaiter().GetResult();
                Assert.True(activate.InputSent, ResultFormatter.Format(activate));
                Assert.Equal(Channels.PhysicalClick, activate.Channel);
            }
            Assert.True(engine.BringToForegroundAsync(window.Hwnd, ct).GetAwaiter().GetResult());

            void Record(int iteration, string operation, ActResult result, string expectedChannel, ActStatus expectedStatus)
            {
                samples.Add(new Sample(iteration, iteration < warmupCount, operation, result.Status.ToString(),
                    result.InputSent, result.Channel, result.Timing.TotalMs,
                    new Dictionary<string, double>(result.Timing.PhasesMs), result.Reason,
                    diagnostic is null ? null : new Dictionary<string, DiagnosticTiming>(diagnostic)));
                Assert.True(result.InputSent, ResultFormatter.Format(result));
                Assert.Equal(expectedChannel, result.Channel);
                Assert.True(result.Status == expectedStatus, ResultFormatter.Format(result));
            }

            for (var i = 0; i < warmupCount + measuredCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var observed = Observe();
                var click = engine.ActAsync(new DesktopActRequest(Ref(observed, "button", "Apply"), observed.Generation, "click",
                    Expect: new ExpectSpec(new TextAppears($"Applied {i + 1}", window.Id))), ct).GetAwaiter().GetResult();
                Record(i, "click", click, Channels.PhysicalClick, ActStatus.Confirmed);
                observed = Observe();
                Assert.Contains($"text \"Applied {i + 1}\"", observed.Text, StringComparison.Ordinal);

                var editor = Ref(observed, "edit", "Editor");
                var focus = engine.ActAsync(new DesktopActRequest(editor, observed.Generation, "focus"), ct).GetAwaiter().GetResult();
                Record(i, "focus_setup", focus, Channels.Api, ActStatus.Confirmed);
                var keys = engine.SendKeysAsync(new KeysRequest("ctrl+a", window.Id), ct).GetAwaiter().GetResult();
                Record(i, "keys", keys, Channels.Keys, ActStatus.Unverified);

                // >16 UTF-16 units exercises multiple focus-checked Unicode batches, including a surrogate pair.
                var text = textLength == 100 ? $"Benchmark {i:D3} " + new string('x', 86) : $"Benchmark {i:D3} café Ω 🚀";
                Assert.Equal(textLength, text.Length);
                observed = Observe();
                var type = engine.ActAsync(new DesktopActRequest(Ref(observed, "edit", "Editor"), observed.Generation, "type", text), ct)
                    .GetAwaiter().GetResult();
                Record(i, "type", type, Channels.UnicodeTyping, ActStatus.Confirmed);
                Assert.Contains($"value={ResultFormatter.Quote(text, 4096)}", Observe().Text, StringComparison.Ordinal);
                if (i == warmupCount - 1 || (i - warmupCount + 1) % 25 == 0)
                    output($"Completed {Math.Max(0, i - warmupCount + 1)}/{measuredCount} measured cycles ({warmupCount} warmup cycles).");
            }
            Assert.Contains("text \"Delete count 0\"", Observe().Text, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            try
            {
                if (engine is not null) engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            finally
            {
                if (!fixture.HasExited)
                {
                    fixture.CloseMainWindow();
                    if (!fixture.WaitForExit(3000)) fixture.Kill(entireProcessTree: true);
                }
                WriteReport(output, samples, metrics, started, warmupCount, measuredCount, textLength, failure);
            }
        }
    }

    private static void WriteReport(Action<string> output, List<Sample> samples, RollingMetrics metrics,
        DateTimeOffset started, int warmupCount, int measuredCount, int textLength, string? failure)
    {
        var series = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var sample in samples.Where(sample => !sample.Warmup))
        {
            Add($"{sample.Operation}.total", sample.TotalMs);
            Add($"{sample.Operation}.checks", sample.PhasesMs.GetValueOrDefault("resolve") + sample.PhasesMs.GetValueOrDefault("precheck"));
            foreach (var (phase, elapsed) in sample.PhasesMs) Add($"{sample.Operation}.{phase}", elapsed);
            if (sample.Diagnostics is { } diagnostics)
                foreach (var (name, timing) in diagnostics) Add($"{sample.Operation}.diagnostic.{name}", timing.TotalMs);
        }
        var summaries = series.ToDictionary(pair => pair.Key, pair => Summarize(pair.Value), StringComparer.Ordinal);
        foreach (var (name, summary) in summaries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            output($"{name} p50={summary.P50:F3}ms p95={summary.P95:F3}ms p99={summary.P99:F3}ms n={summary.Count}");
        var report = new
        {
            StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription, Configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name,
            Binaries = BenchmarkArtifacts.Hashes(FixturePath()),
            WarmupCycles = warmupCount, RequestedMeasuredCycles = measuredCount,
            TextLengthUtf16 = textLength, DiagnosticsEnabled = Environment.GetEnvironmentVariable("OMNIWIN_BENCHMARK_PROFILE") == "1",
            ProductionSafetyHooks = true, UserActivityCooldownMs = 500, Failure = failure,
            Summaries = summaries, Samples = samples,
            // Includes discovery, startup and warmup; action summaries above exclude those samples.
            SessionMetrics = metrics.Snapshot().Ops.ToDictionary(pair => pair.Key,
                pair => new { pair.Value.P50, pair.Value.P95, pair.Value.P99, pair.Value.Count }),
        };
        var path = Environment.GetEnvironmentVariable("OMNIWIN_BENCHMARK_OUTPUT") ??
            Path.Combine(AppContext.BaseDirectory, "TestResults", "desktop-input-benchmark.json");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        output($"Raw benchmark report: {path}");

        void Add(string name, double elapsed)
        {
            if (!series.TryGetValue(name, out var values)) series[name] = values = [];
            values.Add(elapsed);
        }
    }

    private static Summary Summarize(List<double> values)
    {
        values.Sort();
        double Rank(double p) => values[Math.Clamp((int)Math.Ceiling(p * values.Count) - 1, 0, values.Count - 1)];
        return new Summary(values.Count, Rank(0.5), Rank(0.95), Rank(0.99), values[0], values[^1]);
    }

    private static string Ref(Observation observation, string role, string name)
    {
        var match = Regex.Match(observation.Text, @"^\s*(e\d+) " + Regex.Escape(role) + " " +
            Regex.Escape(ResultFormatter.Quote(name, ResultFormatter.MaxNameChars)) + @"(?:\s|$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not find {role} {name}.\n{observation.Text}");
        return match.Groups[1].Value;
    }

    private static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = directory.Name;
        var configuration = directory.Parent!.Name;
        while (!File.Exists(Path.Combine(directory.FullName, "OmniWin.sln")) && directory.Parent is not null)
            directory = directory.Parent;
        var path = Path.Combine(directory.FullName, "testapps", "OmniWin.FixtureApp", "bin", configuration, framework, "OmniWin.FixtureApp.exe");
        Assert.True(File.Exists(path), $"Build the fixture first: {path}");
        return path;
    }

    private sealed record Sample(int Iteration, bool Warmup, string Operation, string Status, bool InputSent,
        string? Channel, double TotalMs, Dictionary<string, double> PhasesMs, string? Reason,
        Dictionary<string, DiagnosticTiming>? Diagnostics);
    private sealed record Summary(int Count, double P50, double P95, double P99, double Min, double Max);
}
