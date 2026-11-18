using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Integration.Harness;

namespace OmniWin.Bench;

internal sealed record AccuracyResult(int Runs, int Steps, int WrongTarget, int InputSentMismatch, int FalseConfirmed, int StepsAsExpected, int RunsFullyOk);

internal sealed record E2EResult(
    string Started, string BrowserVersion, string Configuration, int Warmup, int Samples, IReadOnlyList<Row> Rows,
    AccuracyResult Accuracy, IReadOnlyList<string> NotMeasured, string? Failure);

/// <summary>
/// End-to-end latency over MCP stdio against a real server and a headless browser. No real input. Each row records the
/// status and input_sent of every sample, the client round trip, and the server's own Timing from the result's first line.
/// </summary>
internal static partial class E2E
{
    private const int Warmup = 20;
    private const int Samples = 200;

    [GeneratedRegex(@"diff t[0-9]+ gen [0-9]+->([0-9]+)")]
    private static partial Regex DiffGen();

    [GeneratedRegex(@"\bgen=([0-9]+)")]
    private static partial Regex HeaderGen();

    public static async Task<E2EResult> RunAsync(string root)
    {
        var exe = Paths.ServerExe();
        await using var web = new FixtureWebServer(Paths.Fixtures(root));
        web.Start();
        var rows = new List<Row>();
        string? failure = null;
        AccuracyResult accuracy = new(0, 0, 0, 0, 0, 0, 0);

        try
        {
            rows.Add(await RunWithServerAsync(exe, "tool_call_overhead", "Tool-call overhead (client round trip of omniwin_status; server overhead is not separately visible)", null, "client_round_trip",
                async (s, row) =>
                {
                    await Loop(row, async _ => await s.ToolAsync("omniwin_status", new { }));
                }));

            rows.Add(await RunWithServerAsync(exe, "browser_observe_live_150", "browser_observe from live index (<=150 elements)", 10, "server_metric:browser_observe",
                async (s, row) =>
                {
                    await s.ToolAsync("browser_nav", new { action = "navigate", url = web.Url("elements150.html") });
                    await Loop(row, async _ => await s.ToolAsync("browser_observe", new { }));
                    await AttachMetricsAsync(s, row, "browser_observe");
                }));

            rows.Add(await RunWithServerAsync(exe, "browser_observe_cold", "browser_observe cold (report only)", null, "client_round_trip",
                async (s, row) =>
                {
                    for (var i = 0; i < 50; i++)
                    {
                        await s.ToolAsync("browser_nav", new { action = "navigate", url = web.Url("elements150.html") + "?cold=" + i });
                        row.Samples.Add(Stats.ToSample(await s.ToolAsync("browser_observe", new { })));
                    }
                }));

            foreach (var (action, value) in new[] { ("set_value", "bench"), ("click", null), ("type", "x") })
            {
                rows.Add(await RunWithServerAsync(exe, "browser_act_" + action, "browser_act excluding page reaction", 10, "server_total_without_gate",
                    async (s, row) =>
                    {
                        await s.ToolAsync("browser_nav", new { action = "navigate", url = web.Url("elements150.html") });
                        var obs = (await s.ToolAsync("browser_observe", new { })).Text;
                        var gen = long.Parse(HeaderGen().Match(obs).Groups[1].Value);
                        var target = action == "click" ? RefOf(obs, "Btn 1") : RefOf(obs, "Bench input");
                        await Loop(row, async i =>
                        {
                            var args = new Dictionary<string, object?> { ["ref"] = target, ["gen"] = gen, ["action"] = action };
                            if (value is not null) args["value"] = action == "set_value" ? value + i : value;
                            var r = await s.ToolAsync("browser_act", args);
                            var next = DiffGen().Match(r.Text);
                            if (next.Success) gen = long.Parse(next.Groups[1].Value);
                            return r;
                        });
                        await AttachMetricsAsync(s, row, "browser_act");
                    }));
            }

            rows.Add(await RunWithServerAsync(exe, "windows_list", "windows_list", 5, "server_metric:windows_list",
                async (s, row) =>
                {
                    await Loop(row, async _ => await s.ToolAsync("windows_list", new { }));
                    await AttachMetricsAsync(s, row, "windows_list");
                }));
            rows.Add(await RunWithServerAsync(exe, "audio_read", "audio (read; this PC has no output device, so this is the no-device path)", 5, "server_metric:audio",
                async (s, row) =>
                {
                    await Loop(row, async _ => await s.ToolAsync("audio", new { }));
                    await AttachMetricsAsync(s, row, "audio");
                }));
            rows.Add(await RunWithServerAsync(exe, "system_info_time", "system_info (report only)", null, "client_round_trip",
                async (s, row) => await Loop(row, async _ => await s.ToolAsync("system_info", new { kind = "time" }))));

            rows.Add(await RunWithServerAsync(exe, "workflow_run_step", "workflow_run per step, excluding app reaction", 20, "server_metric:workflow_run_step",
                async (s, row) =>
                {
                    await s.ToolAsync("browser_nav", new { action = "navigate", url = web.Url("elements150.html") });
                    var obs = (await s.ToolAsync("browser_observe", new { })).Text;
                    var gen = HeaderGen().Match(obs).Groups[1].Value;
                    var set = await s.ToolAsync("browser_act", new { @ref = RefOf(obs, "Bench input"), gen = long.Parse(gen), action = "set_value", value = "seed" });
                    var step = set.Tokens["step"];
                    var save = await s.ToolAsync("workflow_save", new { name = "bench", description = "one step", from_step = step, to_step = step });
                    var file = Directory.GetFiles(Path.Combine(s.DataDir, "workflows"), "bench.json").Single();
                    var param = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement.GetProperty("params").EnumerateObject().Single().Name;
                    await Loop(row, async i =>
                    {
                        await s.ToolAsync("browser_nav", new { action = "navigate", url = web.Url("elements150.html") });
                        await s.ToolAsync("browser_observe", new { });
                        return await s.ToolAsync("workflow_run", new { name = "bench", @params = new Dictionary<string, string> { [param] = "v" + i } });
                    }, count: 60, warmup: 5);
                    await AttachMetricsAsync(s, row, "workflow_run_step");
                    if (!save.FirstLine.Contains("saved")) throw new InvalidOperationException("workflow_save: " + save.FirstLine);
                }));

            var launch = new Row("launch_to_first_observe", "browser launch to first observation (report only)", null, "client_round_trip");
            for (var i = 0; i < 5; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var dir = Paths.NewDataDir();
                await using var s = await McpServer.StartAsync(exe, dir);
                await s.ToolAsync("browser_nav", new { action = "navigate", url = web.Url("elements150.html") });
                var obs = await s.ToolAsync("browser_observe", new { });
                launch.Samples.Add(new Sample("ok", false, null, sw.Elapsed.TotalMilliseconds, null, null, null));
                _ = obs;
            }

            rows.Add(launch);
            accuracy = await AccuracyAsync(exe, web);
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }

        var edge = BrowserVersion();
        return new E2EResult(DateTimeOffset.Now.ToString("o"), edge, "Release", Warmup, Samples, rows, accuracy,
        [
            "desktop_observe live/cold, act pre-checks, input dispatch (click, type 100 chars), verify: need a visible fixture window and real input: Phase 5.",
            "screen_ocr (one 1920x1080 window): needs an interactive desktop; this session's virtual screen is 640x480 (WS3 note): Phase 5.",
            "media and window_manage timing: send real keys / move real windows: Phase 5.",
            "Windows-MCP baseline columns (report sections C and D): not run: needs a download the owner has not approved.",
        ], failure);
    }

    private static async Task<Row> RunWithServerAsync(string exe, string name, string section15, double? budget, string basis, Func<McpServer, Row, Task> body)
    {
        var row = new Row(name, section15, budget, basis);
        var dir = Paths.NewDataDir();
        await using var server = await McpServer.StartAsync(exe, dir);
        try
        {
            await body(server, row);
        }
        finally
        {
            await server.DisposeAsync();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { /* browser profile still closing */ }
        }

        return row;
    }

    private static async Task Loop(Row row, Func<int, Task<ToolResult>> call, int count = Samples, int warmup = Warmup)
    {
        for (var i = 0; i < warmup; i++) await call(-1 - i);
        for (var i = 0; i < count; i++) row.Samples.Add(Stats.ToSample(await call(i)));
    }

    private static async Task AttachMetricsAsync(McpServer s, Row row, params string[] ops)
    {
        var all = Stats.ParseMetrics((await s.ToolAsync("omniwin_status", new { })).Text);
        foreach (var op in ops)
        {
            if (all.TryGetValue(op, out var m)) row.ServerMetrics[op] = m;
        }
    }

    private static string RefOf(string obs, string contains) =>
        obs.Split('\n').First(l => l.Contains(contains, StringComparison.Ordinal) && l.TrimStart().StartsWith('b')).Trim().Split(' ')[0];

    private static string BrowserVersion()
    {
        try
        {
            var path = new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" }.First(File.Exists);
            return "Microsoft Edge " + System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// The accuracy gates over a 20-run set of the browser task B09 (fill W01 and press Continue), checked against the
    /// page's own event trace and the fixture server's record of the form: wrong-target actions, input_sent mismatches, false confirmed.
    /// </summary>
    private static async Task<AccuracyResult> AccuracyAsync(string exe, FixtureWebServer web)
    {
        const int runs = 20;
        int steps = 0, wrong = 0, mismatch = 0, falseConfirmed = 0, asExpected = 0, runsOk = 0;
        var dir = Paths.NewDataDir();
        await using var s = await McpServer.StartAsync(exe, dir);
        for (var run = 0; run < runs; run++)
        {
            web.Clear();
            await s.ToolAsync("browser_nav", new { action = "navigate", url = web.Url("w01.html") });
            var plan = new (string Element, string Action, string? Value, string ExpectedId, string ExpectedStatus)[]
            {
                ("Full text", "set_value", "Ada " + run, "w01-text", "confirmed"),
                ("Email address", "set_value", $"ada{run}@example.invalid", "w01-email", "confirmed"),
                ("Choice", "select", "beta", "w01-select", "confirmed"),
                ("I agree", "check", null, "w01-check", "confirmed"),
                ("Notes", "set_value", "run " + run, "w01-area", "confirmed"),
                ("Continue", "click", null, "w01-continue", "unverified"),
            };
            var runOk = true;
            foreach (var step in plan)
            {
                var obs = (await s.ToolAsync("browser_observe", new { })).Text;
                var gen = long.Parse(HeaderGen().Match(obs).Groups[1].Value);
                var before = web.Events.Count;
                var args = new Dictionary<string, object?> { ["ref"] = RefOf(obs, step.Element), ["gen"] = gen, ["action"] = step.Action };
                if (step.Value is not null) args["value"] = step.Value;
                var r = Stats.ToSample(await s.ToolAsync("browser_act", args));
                await Task.Delay(250);
                var fresh = web.Events.Skip(before).ToList();
                steps++;
                if (fresh.Any(e => e.Id != step.ExpectedId)) wrong++;                               // an input on any other control
                if (r.InputSent != fresh.Count > 0) mismatch++;                                      // input_sent disagrees with the page's log
                if (r.Status == step.ExpectedStatus) asExpected++; else runOk = false;
                if (r.Status == "confirmed" && fresh.Count == 0) falseConfirmed++;                   // confirmed with nothing logged
            }

            var form = web.Forms.LastOrDefault();
            if (form is null)
            {
                runOk = false;
            }
            else
            {
                using var doc = JsonDocument.Parse(form);
                var root = doc.RootElement;
                var matches = root.GetProperty("text").GetString() == "Ada " + run &&
                              root.GetProperty("email").GetString() == $"ada{run}@example.invalid" &&
                              root.GetProperty("choice").GetString() == "beta" && root.GetProperty("agree").GetBoolean() &&
                              root.GetProperty("notes").GetString() == "run " + run;
                if (!matches) { runOk = false; falseConfirmed++; }                                   // every step said confirmed but the form is wrong
            }

            if (runOk) runsOk++;
        }

        await s.DisposeAsync();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { /* ignore */ }
        return new AccuracyResult(runs, steps, wrong, mismatch, falseConfirmed, asExpected, runsOk);
    }
}
