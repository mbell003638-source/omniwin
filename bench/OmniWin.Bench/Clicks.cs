using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Integration.Harness;

namespace OmniWin.Bench;

/// <summary>
/// The click-speed benchmark (D2): real <c>desktop_act click</c> calls on the F01 fixture through the published server.
/// Two cases are measured, because a click's total time is mostly what happens after the click:
///   1. "expect_met": a click on the Agree checkbox with a <c>toggle_is</c> expectation, which ends as soon as the box changes;
///   2. "no_change": a click on the Apply button with no expectation. Nothing visible changes, so the act waits its full
///      300 ms settle ceiling before answering "unverified" ("No visible change."). These clicks run in their own loop after
///      one unmeasured warm-up click on Apply: a click right after an Agree click would move keyboard focus, which is a
///      visible change that ends the wait at once and so would not measure this path.
/// Each case reports per-phase times (resolve, precheck, dispatch, verify), how many clicks the fixture's own log saw, and
/// how many results said "No visible change." (the proof of which verify path ran).
/// Real input: needs OMNIWIN_LIVE_SESSION=1 and a connected desktop. Writes a new file under artifacts/benchmarks/.
/// </summary>
internal static partial class Clicks
{
    [GeneratedRegex(@"\bgen=([0-9]+)")]
    private static partial Regex GenRegex();

    [GeneratedRegex(@"total=([0-9.]+)ms \(([^)]*)\)")]
    private static partial Regex TimingRegex();

    private sealed record Click(string Status, bool InputSent, double RoundTripMs, double? TotalMs, IReadOnlyDictionary<string, double> Phases,
        bool NoVisibleChange);

    public static async Task<int> RunAsync(string root, int runs)
    {
        var started = DateTimeOffset.Now;
        if (!DesktopAvailability.IsAvailable(out var why))
        {
            Console.Error.WriteLine("not run: no connected desktop: " + why);
            return 3;
        }

        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var fixtureExe = Path.Combine(root, "testapps", "OmniWin.FixtureApp", "bin", output.Parent!.Name, output.Name, "OmniWin.FixtureApp.exe");
        if (!File.Exists(fixtureExe))
        {
            Console.Error.WriteLine("not run: build testapps/OmniWin.FixtureApp first (same configuration): " + fixtureExe);
            return 2;
        }

        var title = "CLICKS-" + Guid.NewGuid().ToString("N")[..6];
        var log = Path.Combine(Path.GetTempPath(), "omniwin-test-clicks-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        using var fixture = new Process
        {
            StartInfo = new ProcessStartInfo(fixtureExe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = false,
            },
        };
        foreach (var a in new[] { "--scenario", "F01", "--title", title, "--log", log, "--stdin-commands" }) fixture.StartInfo.ArgumentList.Add(a);
        string? dataDir = null;
        fixture.Start();
        try
        {
            var ready = false;
            var readyDeadline = Stopwatch.StartNew();
            while (!ready && readyDeadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                var lineTask = fixture.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(20))) != lineTask) break;
                var line = await lineTask;
                if (line is null) break;
                ready = line.Contains("OMNIWIN_FIXTURE_READY", StringComparison.Ordinal);
            }

            if (!ready)
            {
                Console.Error.WriteLine("not run: the F01 fixture window did not appear");
                return 3;
            }

            dataDir = Paths.NewDataDir();
            var server = await McpServer.StartAsync(Paths.ServerExe(), dataDir, null, new Dictionary<string, string>());
            List<Click> met = [], noChange = [];
            const int applyWarmups = 1;
            try
            {
                var on = false;
                for (var i = 0; i < runs; i++)
                {
                    // Case 1: the expectation is met as soon as the checkbox changes, so there is no fixed wait.
                    var obs = (await server.ToolAsync("desktop_observe", new { window = title })).Text;
                    var agree = RefOf(obs, "Agree");
                    on = !on;
                    met.Add(Parse(await server.ToolAsync("desktop_act", new
                    {
                        @ref = agree, gen = Gen(obs), action = "click",
                        expect = new { condition = new { kind = "toggle_is", @ref = agree, state = on ? "on" : "off" }, timeout_ms = 2000 },
                    })));
                }

                // Case 2: its own loop. The warm-up click moves keyboard focus from Agree to Apply (a visible change) and is
                // not measured; after it, a click on Apply changes nothing, so each measured act waits the full 300 ms
                // settle ceiling before answering "unverified".
                for (var i = 0; i < applyWarmups + runs; i++)
                {
                    var obs = (await server.ToolAsync("desktop_observe", new { window = title })).Text;
                    var click = Parse(await server.ToolAsync("desktop_act", new { @ref = RefOf(obs, "Apply"), gen = Gen(obs), action = "click" }));
                    if (i >= applyWarmups) noChange.Add(click);
                }
            }
            finally
            {
                await server.DisposeAsync();
            }

            await Task.Delay(300);
            // The fixture still has its log open for writing, so it is read with sharing allowed.
            var logLines = new List<string>();
            if (File.Exists(log))
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync() is { } line) logLines.Add(line);
            }

            var agreeEvents = logLines.Count(l => l.Contains("\"Agree\"", StringComparison.Ordinal) && l.Contains("checked", StringComparison.Ordinal));
            var applyEvents = logLines.Count(l => l.Contains("\"Apply\"", StringComparison.Ordinal) && l.Contains("click", StringComparison.Ordinal));

            var dir = Path.Combine(root, "artifacts", "benchmarks");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "clicks-v1.json");
            for (var v = 2; File.Exists(path); v++) path = Path.Combine(dir, $"clicks-v{v}.json");
            var doc = new
            {
                started,
                finished = DateTimeOffset.Now,
                what = "desktop_act click on the F01 fixture through the published server (real input)",
                clicks_per_case = runs,
                cases = new object[]
                {
                    Case("expect_met", "click on the Agree checkbox with a toggle_is expectation (no fixed wait)", met, agreeEvents, 0),
                    Case("no_change", "repeated clicks on the Apply button with no expectation, after one unmeasured warm-up click "
                        + "(nothing changes, so each waits the 300 ms settle ceiling)", noChange, applyEvents, applyWarmups),
                },
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("wrote " + path);
            return 0;
        }
        finally
        {
            // The fixture is this benchmark's own process: end it so no window is left open.
            try
            {
                fixture.StandardInput.Close();
                if (!fixture.WaitForExit(2000)) fixture.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                // Already gone.
            }

            // This run's own files only, by their exact paths: the fixture's log and the server's data folder.
            if (await Paths.RemoveOwnTempFileAsync(log, "omniwin-test-clicks-") is { } logLeft) Console.Error.WriteLine("log not removed: " + logLeft);
            if (dataDir is not null && await Paths.RemoveOwnDataDirAsync(dataDir) is { } dataLeft) Console.Error.WriteLine("data folder not removed: " + dataLeft);
        }
    }

    private static object Case(string name, string what, List<Click> clicks, int fixtureEvents, int warmups)
    {
        object? Pcts(IEnumerable<double?> values)
        {
            var v = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            return v.Length == 0 ? null : new { n = v.Length, p50 = Round(Stats.Percentile(v, 0.5)), p95 = Round(Stats.Percentile(v, 0.95)), max = Round(v.Max()) };
        }

        double? Phase(Click c, string key) => c.Phases.TryGetValue(key, out var ms) ? ms : null;
        return new
        {
            name,
            what,
            clicks = clicks.Count,
            warmup_clicks_not_measured = warmups,
            fixture_log_events = fixtureEvents,   // the fixture's own count of clicks that reached the control (warm-ups included)
            statuses = clicks.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count()),
            no_visible_change = clicks.Count(c => c.NoVisibleChange),   // results that waited the full settle ceiling
            input_sent = clicks.Count(c => c.InputSent),
            ms = new
            {
                client_round_trip = Pcts(clicks.Select(c => (double?)c.RoundTripMs)),
                server_total = Pcts(clicks.Select(c => c.TotalMs)),
                resolve = Pcts(clicks.Select(c => Phase(c, "resolve"))),
                precheck = Pcts(clicks.Select(c => Phase(c, "precheck"))),
                gate = Pcts(clicks.Select(c => Phase(c, "gate"))),
                dispatch = Pcts(clicks.Select(c => Phase(c, "input"))),
                verify = Pcts(clicks.Select(c => Phase(c, "verify"))),
            },
        };
    }

    private static double Round(double v) => Math.Round(v, 1);

    private static Click Parse(ToolResult r)
    {
        var phases = new Dictionary<string, double>(StringComparer.Ordinal);
        double? total = null;
        var m = TimingRegex().Match(r.FirstLine);
        if (m.Success)
        {
            total = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            foreach (var part in m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var space = part.LastIndexOf(' ');
                if (space > 0 && double.TryParse(part[(space + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                    phases[part[..space]] = ms;
            }
        }

        return new Click(r.Status ?? "?", r.InputSent == true, r.RoundTripMs, total, phases,
            r.Text.Contains("No visible change.", StringComparison.Ordinal));
    }

    private static long Gen(string obs) => long.Parse(GenRegex().Match(obs).Groups[1].Value, CultureInfo.InvariantCulture);

    private static string RefOf(string obs, string name)
    {
        var m = Regex.Match(obs, @"^\s*(e[0-9]+) [a-z]+ """ + Regex.Escape(name) + @"""", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException($"\"{name}\" is not in the F01 observation");
    }
}
