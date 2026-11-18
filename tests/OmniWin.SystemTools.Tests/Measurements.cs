using System.Diagnostics;
using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

/// <summary>
/// WS3 timings (contract §15), opt-in: set OMNIWIN_WS3_BENCH=1. Read-only against the real machine plus the service pipeline
/// against fake backends (so "pipeline" rows show the cost of OmniWin's own code, not of Windows). Nothing is changed on
/// the PC. Results go to artifacts/benchmarks/ws3-system-v1.json.
/// </summary>
public sealed class Measurements
{
    public static bool Enabled => Environment.GetEnvironmentVariable("OMNIWIN_WS3_BENCH") == "1";

    private const int Warmup = 20;
    private const int Samples = 300;

    [Fact(Skip = "Set OMNIWIN_WS3_BENCH=1 to measure.", SkipUnless = nameof(Enabled))]
    public async Task Measure_system_tools_and_write_the_artifact()
    {
        var ct = TestContext.Current.CancellationToken;
        var rows = new List<object>();

        // ---- real, read-only ----
        var real = new SystemToolsService(new FakeGate([]), new FakeKill(), new FakeActivity(), new FakeMetrics(), new FakeDesktop(),
            new FakeForeground([]), new SystemToolsOptions());

        var firstApps = await Time(() => real.ListAppsAsync(null, ct), 1, 0);
        rows.Add(Row("apps_list", "real Start menu, first call (cold shell read)", firstApps));
        rows.Add(Row("apps_list", "real, warm (cached for 60 s)", await Time(() => real.ListAppsAsync(null, ct), Samples, Warmup)));

        foreach (var kind in new[] { "time", "battery", "displays", "network" })
        {
            rows.Add(Row("system_info", $"real, kind={kind}", await Time(() => real.SystemInfoAsync(kind, ct), Samples, Warmup)));
        }

        var audioConditions = "real device read";
        try
        {
            await real.GetAudioAsync(ct);
        }
        catch (SystemToolFault)
        {
            audioConditions = "real: this PC has no audio output device, so this is the no-device path";
        }

        rows.Add(Row("audio read", audioConditions, await Time(async () =>
        {
            try { await real.GetAudioAsync(ct); } catch (SystemToolFault) { }
        }, Samples, Warmup)));

        // ---- pipeline cost with fake backends (OmniWin's own code: validation, gate call, readback, timings) ----
        var h = new Harness();
        h.AddWindow("w1", 1001, "fixture.exe", "Fixture");
        rows.Add(Row("window_manage", "fake window backend: service pipeline only (move)",
            await Time(() => h.Service.WindowManageAsync(new WindowManageRequest("w1", "move", X: 10, Y: 10), ct), Samples, Warmup)));
        rows.Add(Row("audio set", "fake audio backend: service pipeline only", await Time(() => h.Service.SetAudioAsync(30, null, ct), Samples, Warmup)));
        h.Media.StateAfterSend = MediaPlayback.Paused;
        rows.Add(Row("media", "fake media backend, skip (no state wait): service pipeline only", await Time(() => h.Service.MediaAsync("next", ct), Samples, Warmup)));
        rows.Add(Row("clipboard_set", "fake clipboard: service pipeline only", await Time(() => h.Service.ClipboardSetAsync("x", ct), Samples, Warmup)));
        rows.Add(Row("clipboard_get", "fake clipboard: service pipeline only", await Time(() => h.Service.ClipboardGetAsync(ct), Samples, Warmup)));

        var root = FindRepoRoot();
        var path = Path.Combine(root, "artifacts", "benchmarks", "ws3-system-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var doc = new
        {
            started_utc = DateTimeOffset.UtcNow,
            note = "Gate time is excluded from the service's own metric but included here (a fake gate that returns at once). Windows animation and app reaction are not measured.",
            warmup, samples = Samples,
            rows,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, CoreJson.Indented), ct);
    }

    private const int warmup = Warmup;

    private static async Task<List<double>> Time(Func<Task> action, int samples, int warm)
    {
        for (var i = 0; i < warm; i++) await action();
        var list = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await action();
            list.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return list;
    }

    private static async Task<List<double>> Time<T>(Func<Task<T>> action, int samples, int warm) =>
        await Time(async () => { await action(); }, samples, warm);

    private static object Row(string op, string conditions, List<double> ms)
    {
        var sorted = ms.Order().ToList();
        double At(double q) => Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))], 3);
        return new { op, conditions, samples = sorted.Count, p50_ms = At(0.5), p95_ms = At(0.95), p99_ms = At(0.99), max_ms = Math.Round(sorted[^1], 3) };
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OmniWin.sln"))) dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find OmniWin.sln");
    }
}
