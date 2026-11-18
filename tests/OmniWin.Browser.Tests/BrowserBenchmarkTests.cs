using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser.Tests;

public sealed class BrowserBenchmarkTests
{
    public static bool BenchmarkEnabled => Environment.GetEnvironmentVariable("OMNIWIN_BROWSER_BENCHMARK") == "1";

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OmniWin.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root with OmniWin.sln");
    }

    private static string Sha256(string path)
    {
        if (!File.Exists(path)) return "missing";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Fact(Skip = "Set OMNIWIN_BROWSER_BENCHMARK=1 to run browser benchmarks.", SkipUnless = nameof(BenchmarkEnabled))]
    public async Task Run_and_record_ws2_browser_m1_benchmarks_v3()
    {
        var repoRoot = FindRepoRoot();
        var outputPath = Path.Combine(repoRoot, "artifacts", "benchmarks", "ws2-browser-m1-benchmarks-v3.json");

        var fixturesDir = Path.Combine(repoRoot, "testapps", "web-fixtures");
        await using var server = new LocalFixtureServer(fixturesDir);
        server.Start();

        var tempDir = Path.Combine(Path.GetTempPath(), "omniwin-bench-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        var paths = OmniWinPaths.FromOverride(tempDir);
        var config = new OmniWinConfig
        {
            Browser = new BrowserConfig
            {
                Mode = "dedicated",
                ProfileDir = Path.Combine(tempDir, "browser-profile")
            }
        };

        var killSwitch = new TestKillSwitch();
        var userActivity = new TestUserActivity();
        var metrics = new TestMetrics();
        var gate = new SafetyGate(config, killSwitch);

        await using var engine = new BrowserEngine(config, paths, gate, killSwitch, userActivity, metrics);
        var ct = TestContext.Current.CancellationToken;

        string? failure = null;
        var launchSamples = new List<double>();
        var summaries = new Dictionary<string, object>();
        var rawSamples = new Dictionary<string, object>();
        var pageInfo = new Dictionary<string, object>();

        try
        {
            // 1. Launch to first observe (cold) - 5 samples
            for (var i = 0; i < 5; i++)
            {
                var swLaunch = Stopwatch.StartNew();
                var lTemp = Path.Combine(Path.GetTempPath(), "omniwin-launch-" + Guid.NewGuid());
                Directory.CreateDirectory(lTemp);
                var lPaths = OmniWinPaths.FromOverride(lTemp);
                var lConfig = new OmniWinConfig
                {
                    Browser = new BrowserConfig
                    {
                        Mode = "dedicated",
                        ProfileDir = Path.Combine(lTemp, "browser-profile")
                    }
                };
                // Disposed in a finally even when a step fails, so no browser is ever left behind.
                var lEngine = new BrowserEngine(lConfig, lPaths, gate, killSwitch, userActivity, metrics);
                try
                {
                    lEngine.ConnectionCreated = connection => connection.CommandInterceptor = NavigationGuard.Check;
                    await lEngine.EnsureConnectedAsync(headless: true, ct);
                    await lEngine.NavigateAsync(new BrowserNavRequest("navigate", server.BaseUrl + "controls.html"), ct);
                    await lEngine.ObserveAsync(new BrowserObserveRequest(), ct);
                    swLaunch.Stop();
                    launchSamples.Add(swLaunch.Elapsed.TotalMilliseconds);
                }
                finally
                {
                    await lEngine.DisposeAsync();
                }

                try { Directory.Delete(lTemp, true); } catch { }
            }

            engine.ConnectionCreated = connection => connection.CommandInterceptor = NavigationGuard.Check;
            await engine.EnsureConnectedAsync(headless: true, ct);

            // 2. Three pages: controls (small), elements150 (the size the observe budget is written for), large (200+).
            // The click target and the text input are found by name once per page.
            var pages = new (string Key, string File, string ClickName, string TextName)[]
            {
                ("controls", "controls.html", "Click Me", "initial text"),
                ("elements150", "elements150.html", "Btn 1", "Bench input"),
                ("large", "large.html", "Item #1", "Large input"),
            };

            foreach (var (key, file, clickName, textName) in pages)
            {
                await engine.NavigateAsync(new BrowserNavRequest("navigate", server.BaseUrl + file), ct);

                // Cold observe - 50 samples (a fresh navigation each time)
                var cold = new ObserveMeasurementAccumulator(50);
                for (var i = 0; i < 50; i++)
                {
                    await engine.NavigateAsync(new BrowserNavRequest("navigate", server.BaseUrl + file + "?cold=" + i), ct);
                    cold.Add(await engine.ObserveAsync(new BrowserObserveRequest(), ct));
                }

                await engine.NavigateAsync(new BrowserNavRequest("navigate", server.BaseUrl + file), ct);

                // Live observe - 10 warmup + 500 samples
                for (var i = 0; i < 10; i++)
                {
                    await engine.ObserveAsync(new BrowserObserveRequest(), ct);
                }

                var live = new ObserveMeasurementAccumulator(500);
                for (var i = 0; i < 500; i++)
                {
                    live.Add(await engine.ObserveAsync(new BrowserObserveRequest(), ct));
                }

                var obs = await engine.ObserveAsync(new BrowserObserveRequest(), ct);
                var clickRef = obs.Text.Split('\n').First(l => l.Contains(clickName)).Trim().Split(' ')[0];
                var textRef = obs.Text.Split('\n').First(l => l.Contains(textName)).Trim().Split(' ')[0];
                pageInfo[key] = new { file, element_count = obs.ElementCount, omitted = obs.OmittedCount, click_target = clickName, text_target = textName };

                // Each act uses the generation the previous act returned, so the baseline is always a recent observation.
                var gen = obs.Generation;

                async Task<ActResult> ActOnce(string r, string action, string? value)
                {
                    var res = await engine.ActAsync(new BrowserActRequest(r, gen, action, value), ct);
                    if (res.After is not null) gen = res.After.Generation;
                    return res;
                }

                async Task<ActMeasurementAccumulator> Measure(string r, string action, Func<int, string?> value)
                {
                    for (var i = 0; i < 10; i++) await ActOnce(r, action, value(-1 - i));
                    var acc = new ActMeasurementAccumulator(500);
                    for (var i = 0; i < 500; i++) acc.Add(await ActOnce(r, action, value(i)));
                    return acc;
                }

                var click = await Measure(clickRef, "click", _ => null);
                var setValue = await Measure(textRef, "set_value", i => "val" + i);
                var type = await Measure(textRef, "type", _ => "x");

                summaries[$"{key}_observe_live"] = live.ToSummary();
                summaries[$"{key}_observe_cold"] = cold.ToSummary();
                summaries[$"{key}_act_click"] = click.ToSummary();
                summaries[$"{key}_act_set_value"] = setValue.ToSummary();
                summaries[$"{key}_act_type"] = type.ToSummary();

                rawSamples[$"{key}_observe_live"] = live.ToRaw();
                rawSamples[$"{key}_observe_cold"] = cold.ToRaw();
                rawSamples[$"{key}_act_click"] = click.ToRaw();
                rawSamples[$"{key}_act_set_value"] = setValue.ToRaw();
                rawSamples[$"{key}_act_type"] = type.ToRaw();
            }

            summaries["launch_to_first_observe"] = ComputeStats(launchSamples);
            rawSamples["launch_to_first_observe"] = launchSamples;
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            var edgePath = BrowserLauncher.FindBrowserExecutable("msedge");
            var edgeVer = FileVersionInfo.GetVersionInfo(edgePath).FileVersion ?? "unknown";

            // Hashes are of the assemblies this run actually loaded, with their paths.
            object Loaded(Type t) => new { assembly = t.Assembly.GetName().Name, path = t.Assembly.Location, sha256 = Sha256(t.Assembly.Location) };

            var benchmarkDoc = new
            {
                started_utc = DateTimeOffset.UtcNow,
                failure,
                browser_version = $"Microsoft Edge {edgeVer}",
                runtime = $".NET {Environment.Version}",
                os = RuntimeInformation.OSDescription,
                configuration = "Release",
                loaded_assemblies = new[] { Loaded(typeof(BrowserEngine)), Loaded(typeof(OmniWinConfig)), Loaded(typeof(OmniWin.Core.ReasonCodes)) },
                warmup_cycles = 10,
                requested_measured_cycles = 500,
                requested_cold_cycles = 50,
                requested_launch_cycles = 5,
                pages = pageInfo,
                summaries,
                raw_samples = rawSamples
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var json = JsonSerializer.Serialize(benchmarkDoc, CoreJson.Indented);
            await File.WriteAllTextAsync(outputPath, json, CancellationToken.None);

            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static object ComputeStats(List<double> list)
    {
        if (list.Count == 0) return new { count = 0, p50 = 0.0, p95 = 0.0, p99 = 0.0, min = 0.0, max = 0.0, mean = 0.0 };
        var sorted = list.OrderBy(x => x).ToList();
        var count = sorted.Count;
        return new
        {
            count,
            p50 = Math.Round(sorted[(int)(count * 0.50)], 2),
            p95 = Math.Round(sorted[(int)(count * 0.95)], 2),
            p99 = Math.Round(sorted[(int)(count * 0.99)], 2),
            min = Math.Round(sorted[0], 2),
            max = Math.Round(sorted[^1], 2),
            mean = Math.Round(sorted.Average(), 2)
        };
    }

    private sealed class ActMeasurementAccumulator
    {
        public List<double> Total { get; }
        public List<double> Resolve { get; }
        public List<double> Precheck { get; }
        public List<double> Gate { get; }
        public List<double> Input { get; }
        public List<double> Verify { get; }
        public List<string> Status { get; } = [];
        public List<bool> InputSent { get; } = [];
        public List<string?> Reason { get; } = [];

        public ActMeasurementAccumulator(int capacity)
        {
            Total = new List<double>(capacity);
            Resolve = new List<double>(capacity);
            Precheck = new List<double>(capacity);
            Gate = new List<double>(capacity);
            Input = new List<double>(capacity);
            Verify = new List<double>(capacity);
        }

        public void Add(ActResult res)
        {
            Total.Add(res.Timing.TotalMs);
            Status.Add(res.Status.ToString());
            InputSent.Add(res.InputSent);
            Reason.Add(res.Reason);
            if (res.Timing.PhasesMs is not null)
            {
                if (res.Timing.PhasesMs.TryGetValue("resolve", out var r)) Resolve.Add(r);
                if (res.Timing.PhasesMs.TryGetValue("precheck", out var p)) Precheck.Add(p);
                if (res.Timing.PhasesMs.TryGetValue("gate", out var g)) Gate.Add(g);
                if (res.Timing.PhasesMs.TryGetValue("input", out var i)) Input.Add(i);
                if (res.Timing.PhasesMs.TryGetValue("verify", out var v)) Verify.Add(v);
            }
        }

        public Dictionary<string, object> ToSummary() => new()
        {
            ["samples"] = Total.Count,
            ["status_counts"] = Status.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()),
            ["input_sent_true"] = InputSent.Count(x => x),
            ["total"] = ComputeStats(Total),
            ["resolve"] = ComputeStats(Resolve),
            ["precheck"] = ComputeStats(Precheck),
            ["gate"] = ComputeStats(Gate),
            ["input"] = ComputeStats(Input),
            ["verify"] = ComputeStats(Verify)
        };

        public Dictionary<string, object> ToRaw() => new()
        {
            ["status"] = Status,
            ["input_sent"] = InputSent,
            ["reason"] = Reason,
            ["total"] = Total,
            ["resolve"] = Resolve,
            ["precheck"] = Precheck,
            ["gate"] = Gate,
            ["input"] = Input,
            ["verify"] = Verify
        };
    }

    private sealed class ObserveMeasurementAccumulator
    {
        public List<double> Total { get; }
        public List<double> Read { get; }
        public List<double> Format { get; }

        public ObserveMeasurementAccumulator(int capacity)
        {
            Total = new List<double>(capacity);
            Read = new List<double>(capacity);
            Format = new List<double>(capacity);
        }

        public void Add(Observation obs)
        {
            Total.Add(obs.Timing.TotalMs);
            if (obs.Timing.PhasesMs is not null)
            {
                if (obs.Timing.PhasesMs.TryGetValue("read", out var r)) Read.Add(r);
                if (obs.Timing.PhasesMs.TryGetValue("format", out var f)) Format.Add(f);
            }
        }

        public Dictionary<string, object> ToSummary() => new()
        {
            ["total"] = ComputeStats(Total),
            ["read"] = ComputeStats(Read),
            ["format"] = ComputeStats(Format)
        };

        public Dictionary<string, object> ToRaw() => new()
        {
            ["total"] = Total,
            ["read"] = Read,
            ["format"] = Format
        };
    }
}
