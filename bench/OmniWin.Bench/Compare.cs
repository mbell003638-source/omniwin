using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Integration.Harness;

namespace OmniWin.Bench;

/// <summary>
/// Native against bridged (contract 2.5): the same small jobs on the F01 fixture, done three ways through the published server:
/// OmniWin's own tools, Windows-MCP through <c>engine_call</c>, and agent-desktop through <c>engine_call</c>. Each way first
/// looks (observe / snapshot) and then acts, as a model would. The jobs:
///   1. click the Agree checkbox (a visible change),
///   2. click the Apply button (nothing visible changes),
///   3. clear the Name box and type a 200-character text into it.
/// The judge is the fixture's own log, never a tool's answer: a click is exact when its control logged exactly one event and no
/// other control logged anything; a typing run is exact when the Name box's last text equals the text. Each way gets one
/// unmeasured warm-up (it also starts the engine's process, whose time is reported separately).
/// Safety: a Windows-MCP click or type goes to screen coordinates, so before each one the point is checked to belong to the
/// fixture's own window; if not, that step is not run (it can never land in another window). agent-desktop acts on its own
/// element refs inside the fixture app only. Real input: needs OMNIWIN_LIVE_SESSION=1. Writes artifacts/benchmarks/compare-vN.json.
/// </summary>
internal static partial class Compare
{
    private const string WindowsMcp = "windows-mcp";
    private const string AgentDesktop = "agent-desktop";
    private const string Native = "omniwin";
    private const int TypedChars = 200;

    [GeneratedRegex(@"\bgen=([0-9]+)")]
    private static partial Regex GenRegex();

    private sealed record Step(string Way, string Job, string? NotRun, double LookMs, double ActMs, string Status, bool InputSent,
        int TargetEvents, int OtherEvents, bool? Exact, int? ReadChars, string? Note = null);

    private sealed class Fixture(Process process, string title, string log)
    {
        public Process Process { get; } = process;
        public string Title { get; } = title;

        public List<JsonElement> Events()
        {
            var list = new List<JsonElement>();
            if (!File.Exists(log)) return list;
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                list.Add(doc.RootElement.Clone());
            }

            return list;
        }

        public static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public int Count(string control, string ev) => Events().Count(e => Str(e, "event") == ev && Str(e, "control") == control);

        public int InputEvents() => Events().Count(e => Str(e, "event") is
            "click" or "text_changed" or "typed" or "selected" or "checked" or "value" or "expanded" or "collapsed");

        public string? LastText(string control) =>
            Events().LastOrDefault(e => Str(e, "event") == "text_changed" && Str(e, "control") == control) is { ValueKind: JsonValueKind.Object } last
                ? Str(last, "value") : null;
    }

    public static async Task<int> RunAsync(string root, int runs, string? only = null)
    {
        var started = DateTimeOffset.Now;
        if (!DesktopAvailability.IsAvailable(out var why))
        {
            Console.Error.WriteLine("not run: no connected desktop: " + why);
            return 3;
        }

        // Same coordinate space as Windows-MCP (physical pixels), so the point check below is exact.
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4));

        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var fixtureExe = Path.Combine(root, "testapps", "OmniWin.FixtureApp", "bin", output.Parent!.Name, output.Name, "OmniWin.FixtureApp.exe");
        if (!File.Exists(fixtureExe))
        {
            Console.Error.WriteLine("not run: build testapps/OmniWin.FixtureApp first (same configuration): " + fixtureExe);
            return 2;
        }

        var title = "COMPARE-" + Guid.NewGuid().ToString("N")[..6];
        var log = Path.Combine(Path.GetTempPath(), "omniwin-test-compare-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fixtureExe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true },
        };
        foreach (var a in new[] { "--scenario", "F01", "--title", title, "--log", log, "--stdin-commands" }) process.StartInfo.ArgumentList.Add(a);
        string? dataDir = null;
        var steps = new List<Step>();
        var startMs = new Dictionary<string, double>(StringComparer.Ordinal);
        process.Start();
        try
        {
            if (!await WaitReadyAsync(process))
            {
                Console.Error.WriteLine("not run: the F01 fixture window did not appear");
                return 3;
            }

            var f = new Fixture(process, title, log);
            dataDir = Paths.NewDataDir();
            var server = await McpServer.StartAsync(Paths.ServerExe(), dataDir, null, new Dictionary<string, string>());
            try
            {
                var list = (await server.ToolAsync("engines_list", new { })).Text;
                var ways = new List<string> { Native };
                foreach (var engine in new[] { WindowsMcp, AgentDesktop })
                {
                    if (list.Contains(engine, StringComparison.Ordinal) && !list.Contains(engine + " state=disabled", StringComparison.Ordinal)) ways.Add(engine);
                }

                foreach (var way in ways.Where(w => only is null || w == only))
                {
                    // Warm-up: starts the engine process (reported) and moves focus into the fixture once; not measured.
                    var sw = Stopwatch.StartNew();
                    _ = await LookAsync(server, f, way);
                    startMs[way] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                    foreach (var job in new[] { "Agree", "Apply" })
                    {
                        _ = await ClickAsync(server, f, way, job);   // warm-up click, not measured
                        for (var i = 0; i < runs; i++) steps.Add(await ClickAsync(server, f, way, job));
                    }

                    for (var i = 0; i < 3; i++) steps.Add(await TypeAsync(server, f, way, Tasks.TypingText(TypedChars)));
                }
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000)) process.Kill();   // this run's own fixture process
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                // Already gone.
            }

            if (await Paths.RemoveOwnTempFileAsync(log, "omniwin-test-compare-") is { } logLeft) Console.Error.WriteLine("log not removed: " + logLeft);
            if (dataDir is not null && await Paths.RemoveOwnDataDirAsync(dataDir) is { } dataLeft) Console.Error.WriteLine("data folder not removed: " + dataLeft);
        }

        await Task.Delay(1000);
        var toolsDir = Path.Combine(root, "tools");
        var enginesLeft = Process.GetProcesses().Count(p =>
        {
            try
            {
                return p.MainModule?.FileName?.StartsWith(toolsDir, StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
            finally
            {
                p.Dispose();
            }
        });

        var dir = Path.Combine(root, "artifacts", "benchmarks");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "compare-v1.json");
        for (var v = 2; File.Exists(path); v++) path = Path.Combine(dir, $"compare-v{v}.json");
        var doc = new
        {
            started,
            finished = DateTimeOffset.Now,
            what = "the same jobs on the F01 fixture through the published server: OmniWin's own tools against Windows-MCP and agent-desktop " +
                   "through engine_call (real input); judged by the fixture's own log",
            clicks_per_job = runs,
            typing_runs = 3,
            typed_chars = TypedChars,
            engine_start_ms = startMs,
            engine_processes_left = enginesLeft,
            results = steps.GroupBy(s => (s.Way, s.Job)).Select(g => Summary(g.Key.Way, g.Key.Job, [.. g])).ToList(),
            steps,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("wrote " + path);
        return 0;
    }

    private static object Summary(string way, string job, List<Step> s)
    {
        var ran = s.Where(x => x.NotRun is null).ToList();
        object? Pcts(IEnumerable<double> values)
        {
            var v = values.ToArray();
            return v.Length == 0 ? null : new { p50 = Math.Round(Stats.Percentile(v, 0.5), 1), p95 = Math.Round(Stats.Percentile(v, 0.95), 1) };
        }

        return new
        {
            way,
            job,
            runs = s.Count,
            not_run = s.Count - ran.Count,
            exact = ran.Count(x => x.Exact == true),
            missed = ran.Count(x => x.Exact != true && x.TargetEvents == 0),
            extra_or_stray = ran.Count(x => x.TargetEvents > 1 || x.OtherEvents > 0),
            // A result that says "confirmed" when the fixture did not see exactly the requested effect.
            false_success = ran.Count(x => x.Status == "confirmed" && x.Exact != true),
            statuses = ran.GroupBy(x => x.Status).ToDictionary(g => g.Key, g => g.Count()),
            look_ms = Pcts(ran.Select(x => x.LookMs)),
            act_ms = Pcts(ran.Select(x => x.ActMs)),
            look_plus_act_ms = Pcts(ran.Select(x => x.LookMs + x.ActMs)),
            not_run_reasons = s.Where(x => x.NotRun is not null).Select(x => x.NotRun).Distinct().ToList(),
        };
    }

    // ---------------------------------------------------------------- one look, one click, one typing run

    private sealed record Look(double Ms, string Text, long Gen);

    private static async Task<Look> LookAsync(McpServer server, Fixture f, string way)
    {
        var sw = Stopwatch.StartNew();
        var r = way switch
        {
            Native => await server.ToolAsync("desktop_observe", new { window = f.Title }),
            WindowsMcp => await server.ToolAsync("engine_call", new { engine = WindowsMcp, tool = "Snapshot", args = new { use_ui_tree = true } }, 180),
            _ => await server.ToolAsync("engine_call", new { engine = AgentDesktop, tool = "desktop_snapshot", args = new { app = "OmniWin.FixtureApp" } }, 180),
        };
        var ms = sw.Elapsed.TotalMilliseconds;
        var g = GenRegex().Match(r.Text);
        return new Look(ms, r.Text, way == Native && g.Success ? long.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>The element to act on, in the form each way needs: an eN ref, screen coordinates, or an agent-desktop @ref.</summary>
    private static string? Target(string way, string text, string name)
    {
        var quoted = Regex.Escape(name);
        Match m;
        switch (way)
        {
            case Native:
                m = Regex.Match(text, @"^\s*(e[0-9]+) [a-z]+ """ + quoted + @"""", RegexOptions.Multiline);
                return m.Success ? m.Groups[1].Value : null;
            case WindowsMcp:
                m = Regex.Match(text, @"\((\d+),(\d+)\) [\w ]+? \\?""" + quoted + @"\\?""");
                return m.Success ? m.Groups[1].Value + "," + m.Groups[2].Value : null;
            default:
                return AgentDesktopRef(text, name);
        }
    }

    /// <summary>agent-desktop's snapshot is JSON (after OmniWin's header lines): the ref_id of the first node with this exact name.</summary>
    private static string? AgentDesktopRef(string text, string name)
    {
        var start = text.IndexOf("{\"command\"", StringComparison.Ordinal);
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..]);
            return Find(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }

        string? Find(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (Fixture.Str(e, "name") == name && Fixture.Str(e, "ref_id") is { } found) return found;
                foreach (var p in e.EnumerateObject())
                {
                    if (Find(p.Value) is { } inner) return inner;
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    if (Find(item) is { } inner) return inner;
                }
            }

            return null;
        }
    }

    private static async Task<Step> ClickAsync(McpServer server, Fixture f, string way, string job)
    {
        var look = await LookAsync(server, f, way);
        var target = Target(way, look.Text, job);
        if (target is null) return NotRun(way, job, $"\"{job}\" was not in this way's look: " + Sample(look.Text));
        if (way == WindowsMcp && !PointIsFixture(target, f.Process.Id)) return NotRun(way, job, "the point was not on the fixture window");

        var targetEvent = job == "Agree" ? "checked" : "click";
        var beforeTarget = f.Count(job, targetEvent);
        var beforeAll = f.InputEvents();
        var sw = Stopwatch.StartNew();
        var r = way switch
        {
            Native => await server.ToolAsync("desktop_act", new { @ref = target, gen = look.Gen, action = "click" }),
            WindowsMcp => await server.ToolAsync("engine_call", new { engine = WindowsMcp, tool = "Click", args = new { loc = Loc(target) } }, 120),
            _ => await server.ToolAsync("engine_call", new { engine = AgentDesktop, tool = "desktop_click", args = new { ref_id = target } }, 120),
        };
        var actMs = sw.Elapsed.TotalMilliseconds;
        await Task.Delay(400);   // let the fixture write its log line; not part of the timing
        var targetEvents = f.Count(job, targetEvent) - beforeTarget;
        var other = f.InputEvents() - beforeAll - targetEvents;
        return new Step(way, job, null, Math.Round(look.Ms, 1), Math.Round(actMs, 1), r.Status ?? (r.IsError ? "tool_error" : "?"),
            r.InputSent == true, targetEvents, other, targetEvents == 1 && other == 0, null, targetEvents == 1 && other == 0 ? null : Sample(r.Text));
    }

    private static async Task<Step> TypeAsync(McpServer server, Fixture f, string way, string text)
    {
        const string job = "Name";
        // Empty the box first, outside the timing (Windows-MCP clears inside its own Type call: that is part of its time).
        if (way == Native)
        {
            var pre = await LookAsync(server, f, way);
            if (Target(way, pre.Text, job) is { } preRef) _ = await server.ToolAsync("desktop_act", new { @ref = preRef, gen = pre.Gen, action = "clear" });
        }
        else if (way == AgentDesktop)
        {
            var pre = await LookAsync(server, f, way);
            if (Target(way, pre.Text, job) is { } preRef)
                _ = await server.ToolAsync("engine_call", new { engine = AgentDesktop, tool = "desktop_set_value", args = new { ref_id = preRef, value = "" } }, 120);
        }

        var look = await LookAsync(server, f, way);
        var target = Target(way, look.Text, job);
        if (target is null) return NotRun(way, "type", "the Name box was not in this way's look: " + Sample(look.Text));
        if (way == WindowsMcp && !PointIsFixture(target, f.Process.Id)) return NotRun(way, "type", "the point was not on the fixture window");

        var sw = Stopwatch.StartNew();
        var r = way switch
        {
            Native => await server.ToolAsync("desktop_act", new { @ref = target, gen = look.Gen, action = "type", value = text }, 180),
            WindowsMcp => await server.ToolAsync("engine_call", new { engine = WindowsMcp, tool = "Type", args = new { text, loc = Loc(target), clear = true } }, 180),
            _ => await server.ToolAsync("engine_call", new { engine = AgentDesktop, tool = "desktop_type", args = new { ref_id = target, text } }, 180),
        };
        var actMs = sw.Elapsed.TotalMilliseconds;
        await Task.Delay(600);
        var read = f.LastText(job);
        return new Step(way, "type", null, Math.Round(look.Ms, 1), Math.Round(actMs, 1), r.Status ?? (r.IsError ? "tool_error" : "?"),
            r.InputSent == true, 0, 0, read == text, read?.Length, r.Status == "confirmed" || read == text ? null : Sample(r.Text));
    }

    private static Step NotRun(string way, string job, string why) => new(way, job, why, 0, 0, "not_run", false, 0, 0, null, null);

    /// <summary>The start of a look's text, for a step that could not find its element (so the reason can be seen in the file).</summary>
    private static string Sample(string text) => text.Length <= 600 ? text : text[..600];

    private static int[] Loc(string xy) => [.. xy.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture))];

    private static bool PointIsFixture(string xy, int fixturePid)
    {
        var loc = Loc(xy);
        var hwnd = WindowFromPoint(new Point { X = loc[0], Y = loc[1] });
        if (hwnd == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(GetAncestor(hwnd, 2), out var pid);
        return pid == (uint)fixturePid;
    }

    private static async Task<bool> WaitReadyAsync(Process process)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            var lineTask = process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(20))) != lineTask) return false;
            if (await lineTask is not { } line) return false;
            if (line.Contains("OMNIWIN_FIXTURE_READY", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
