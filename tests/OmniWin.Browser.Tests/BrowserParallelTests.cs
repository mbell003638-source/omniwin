using System.Diagnostics;
using System.Text.Json;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser.Tests;

/// <summary>
/// Parallel lanes: calls on different tabs run at the same time and each lands only on its own tab; calls on the same tab
/// take turns; one tab's JavaScript dialog never holds up another tab. Headless, local fixtures, no real input.
/// </summary>
public sealed class BrowserParallelTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private LocalFixtureServer _server = null!;
    private BrowserEngine _engine = null!;
    private TestKillSwitch _killSwitch = null!;

    public async ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _server = new LocalFixtureServer(FindWebFixturesDir());
        _server.Start();

        var paths = OmniWinPaths.FromOverride(_tempDir);
        var config = new OmniWinConfig
        {
            Browser = new BrowserConfig { Mode = "dedicated", ProfileDir = Path.Combine(_tempDir, "browser-profile") }
        };

        _killSwitch = new TestKillSwitch();
        var gate = new SafetyGate(config, _killSwitch);
        _engine = new BrowserEngine(config, paths, gate, _killSwitch, new TestUserActivity(), new TestMetrics());

        // Fail closed: no test can navigate to a non-local URL, even on a connection made after a reconnect.
        _engine.ConnectionCreated = connection => connection.CommandInterceptor = NavigationGuard.Check;
        await _engine.EnsureConnectedAsync(headless: true, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Both always run, even when a test failed or setup stopped partway: the browser is stopped by its profile folder.
        try
        {
            if (_engine is not null) await _engine.DisposeAsync();
        }
        finally
        {
            if (_server is not null) await _server.DisposeAsync();
        }
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // temp folder cleanup is best effort
        }
    }

    private CdpSession SessionOf(string tab) =>
        (CdpSession)_engine.GetType()
            .GetMethod("GetSessionForTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, [tab])!;

    private async Task<string> LogOf(string tab, CancellationToken ct) =>
        await SessionOf(tab).EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RefOfLine(Observation obs, string contains) =>
        obs.Text.Split('\n').First(l => l.Contains(contains)).Trim().Split(' ')[0];

    /// <summary>Opens the first fixture in the current tab and the second in a new tab. Returns both tab ids; the new tab is active.</summary>
    private async Task<(string A, string B)> OpenTwoTabsAsync(string fixtureA, string fixtureB, CancellationToken ct)
    {
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + fixtureA), ct);
        var tabA = (await _engine.ListTabsAsync(ct)).First(t => t.Active).Id;

        await _engine.NavigateAsync(new BrowserNavRequest("new_tab", _server.BaseUrl + fixtureB), ct);
        var tabB = (await _engine.ListTabsAsync(ct)).First(t => t.Active).Id;
        Assert.NotEqual(tabA, tabB);
        return (tabA, tabB);
    }

    private async Task<Observation> ObserveWhenReadyAsync(string tab, string contains, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var obs = await _engine.ObserveAsync(new BrowserObserveRequest(Tab: tab), ct);
                if (obs.Text.Contains(contains, StringComparison.Ordinal)) return obs;
            }
            catch (Exception) when (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                // the new tab is still attaching
            }

            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), $"tab {tab} never showed '{contains}'");
            await Task.Delay(100, ct);
        }
    }

    [Fact(Timeout = 180000)]
    public async Task Acts_on_two_tabs_at_once_each_land_only_on_their_own_tab_for_50_rounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tabA, tabB) = await OpenTwoTabsAsync("controls.html", "js-dialog.html", ct);   // tab B is the active tab
        var obsA = await ObserveWhenReadyAsync(tabA, "Click Me", ct);
        var obsB = await ObserveWhenReadyAsync(tabB, "Plain button", ct);
        var refA = RefOfLine(obsA, "Click Me");
        var refB = RefOfLine(obsB, "Plain button");

        // Refs are unique across tabs.
        var refsA = obsA.Text.Split('\n').Select(l => l.Trim().Split(' ')[0]).Where(r => r.StartsWith('b')).ToHashSet();
        var refsB = obsB.Text.Split('\n').Select(l => l.Trim().Split(' ')[0]).Where(r => r.StartsWith('b')).ToHashSet();
        Assert.Empty(refsA.Intersect(refsB));

        long genA = obsA.Generation, genB = obsB.Generation;
        for (var round = 0; round < 50; round++)
        {
            var a = _engine.ActAsync(new BrowserActRequest(refA, genA, "click"), ct);
            var b = _engine.ActAsync(new BrowserActRequest(refB, genB, "click"), ct);
            var results = await Task.WhenAll(a, b);

            Assert.True(results[0].InputSent, $"round {round} A: {results[0].Status}/{results[0].Reason}/{results[0].Detail}");
            Assert.True(results[1].InputSent, $"round {round} B: {results[1].Status}/{results[1].Reason}/{results[1].Detail}");
            genA = results[0].After!.Generation;
            genB = results[1].After!.Generation;
        }

        var logA = await LogOf(tabA, ct);
        var logB = await LogOf(tabB, ct);
        Assert.Equal(50, Occurrences(logA, "click:btn-click-me"));
        Assert.Equal(0, Occurrences(logA, "click:plain"));
        Assert.Equal(50, Occurrences(logB, "click:plain"));
        Assert.Equal(0, Occurrences(logB, "click:btn-click-me"));
    }

    [Fact(Timeout = 120000)]
    public async Task A_ref_names_its_tab_so_describe_and_act_use_that_tab_not_the_active_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tabA, tabB) = await OpenTwoTabsAsync("controls.html", "keys.html", ct);   // B is active
        var obsA = await ObserveWhenReadyAsync(tabA, "Click Me", ct);
        var obsB = await ObserveWhenReadyAsync(tabB, "Search box", ct);

        var descA = await _engine.DescribeAsync(RefOfLine(obsA, "Click Me"), ct);
        var descB = await _engine.DescribeAsync(RefOfLine(obsB, "Search box"), ct);

        Assert.NotNull(descA);
        Assert.NotNull(descB);
        Assert.Contains("controls.html", descA.Url);
        Assert.Contains("keys.html", descB.Url);

        // An act on tab A's ref while tab B is the active tab runs on tab A.
        var res = await _engine.ActAsync(new BrowserActRequest(RefOfLine(obsA, "Click Me"), obsA.Generation, "click"), ct);
        Assert.True(res.InputSent, $"{res.Status}/{res.Reason}/{res.Detail}");
        Assert.Contains("click:btn-click-me", await LogOf(tabA, ct));
        Assert.DoesNotContain("click:btn-click-me", await LogOf(tabB, ct));
    }

    [Fact(Timeout = 120000)]
    public async Task Concurrent_acts_on_the_same_tab_take_turns_so_their_mouse_events_never_interleave()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var tab = (await _engine.ListTabsAsync(ct)).First(t => t.Active).Id;
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(Tab: tab), ct);
        var clickMe = RefOfLine(obs, "Click Me");

        // A press must always be followed by its release before the next press starts.
        var pressed = false;
        var overlaps = 0;
        var conn = _engine.Connection!;
        conn.CommandInterceptor = NavigationGuard.With((method, parameters) =>
        {
            if (method == "Input.dispatchMouseEvent")
            {
                var json = JsonSerializer.Serialize(parameters);
                if (json.Contains("mousePressed"))
                {
                    if (pressed) Interlocked.Increment(ref overlaps);
                    pressed = true;
                }
                else if (json.Contains("mouseReleased"))
                {
                    pressed = false;
                }
            }

            return Task.CompletedTask;
        });

        try
        {
            var acts = Enumerable.Range(0, 6).Select(_ => _engine.ActAsync(new BrowserActRequest(clickMe, obs.Generation, "click"), ct)).ToArray();
            var results = await Task.WhenAll(acts);

            Assert.All(results, r => Assert.True(r.InputSent, $"{r.Status}/{r.Reason}/{r.Detail}"));
            Assert.Equal(0, overlaps);
            Assert.Equal(6, Occurrences(await LogOf(tab, ct), "click:btn-click-me"));
        }
        finally
        {
            conn.CommandInterceptor = NavigationGuard.Check;
        }
    }

    [Fact(Timeout = 120000)]
    public async Task A_dialog_open_on_one_tab_does_not_hold_up_another_tab()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tabA, tabB) = await OpenTwoTabsAsync("js-dialog.html", "controls.html", ct);
        var obsA = await ObserveWhenReadyAsync(tabA, "Show alert", ct);
        var obsB = await ObserveWhenReadyAsync(tabB, "Click Me", ct);

        // Open an alert on tab A (the act returns as soon as the dialog opens).
        var open = await _engine.ActAsync(new BrowserActRequest(RefOfLine(obsA, "Show alert"), obsA.Generation, "click"), ct);
        Assert.True(open.InputSent);
        Assert.True(SessionOf(tabA).HasOpenDialog);

        // Tab B keeps working at full speed while A is frozen.
        var sw = Stopwatch.StartNew();
        var res = await _engine.ActAsync(new BrowserActRequest(RefOfLine(obsB, "Click Me"), obsB.Generation, "click"), ct);
        var observed = await _engine.ObserveAsync(new BrowserObserveRequest(Tab: tabB), ct);
        Assert.True(res.InputSent, $"{res.Status}/{res.Reason}/{res.Detail}");
        Assert.Contains("Click Me", observed.Text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "tab B was held up: " + sw.Elapsed);
        Assert.Contains("click:btn-click-me", await LogOf(tabB, ct));

        // Answer the dialog on A without touching B.
        var dismiss = await _engine.NavigateAsync(new BrowserNavRequest("dialog_dismiss", Tab: tabA), ct);
        Assert.True(dismiss.InputSent);
        Assert.Contains("alert:closed", await LogOf(tabA, ct));
    }

    [Fact(Timeout = 60000)]
    public async Task The_stop_key_ends_a_wait_for_a_busy_tab_with_denied_and_nothing_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var tab = (await _engine.ListTabsAsync(ct)).First(t => t.Active).Id;
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(Tab: tab), ct);
        var clickMe = RefOfLine(obs, "Click Me");

        // Hold the tab's gate as if another act were running, then ask for a second act and trip the stop key while it waits.
        var session = SessionOf(tab);
        await session.OpGate.WaitAsync(ct);
        try
        {
            var waiting = _engine.ActAsync(new BrowserActRequest(clickMe, obs.Generation, "click"), ct);
            await Task.Delay(300, ct);
            Assert.False(waiting.IsCompleted);   // it is queued behind the busy tab

            _killSwitch.Trip();
            var res = await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct);

            Assert.Equal(ActStatus.Denied, res.Status);
            Assert.Equal(ReasonCodes.KillSwitch, res.Reason);
            Assert.False(res.InputSent);
        }
        finally
        {
            session.OpGate.Release();
        }
    }

    [Fact(Skip = "Set OMNIWIN_PARALLEL_BENCH=1 to measure parallel against serial.", SkipUnless = nameof(BenchEnabled), Timeout = 600000)]
    public async Task Measure_parallel_against_serial_on_two_tabs()
    {
        var ct = TestContext.Current.CancellationToken;
        var actsPerTab = int.TryParse(Environment.GetEnvironmentVariable("OMNIWIN_PARALLEL_ACTS"), out var configured) && configured > 0 ? configured : 100;
        var (tabA, tabB) = await OpenTwoTabsAsync("elements150.html", "large.html", ct);
        var obsA = await ObserveWhenReadyAsync(tabA, "Btn 1", ct);
        var obsB = await ObserveWhenReadyAsync(tabB, "Item #1", ct);
        var refA = RefOfLine(obsA, "Btn 1");
        var refB = RefOfLine(obsB, "Item #1");

        async Task<(double Ms, int Ok)> RunAsync(bool parallel)
        {
            // Each run starts from a fresh observation: a tab keeps only its last few generations, so the generation seen
            // before an earlier run would be gone.
            long genA = (await _engine.ObserveAsync(new BrowserObserveRequest(Tab: tabA), ct)).Generation;
            long genB = (await _engine.ObserveAsync(new BrowserObserveRequest(Tab: tabB), ct)).Generation;
            var ok = 0;
            string? firstFailure = null;

            async Task LoopAsync(string r, Func<long> getGen, Action<long> setGen)
            {
                for (var i = 0; i < actsPerTab; i++)
                {
                    var res = await _engine.ActAsync(new BrowserActRequest(r, getGen(), "click"), ct);
                    if (res.InputSent && res.After is not null)
                    {
                        Interlocked.Increment(ref ok);
                        setGen(res.After.Generation);
                    }
                    else
                    {
                        firstFailure ??= $"{res.Status}/{res.Reason}/{res.Detail}";
                        Assert.Fail("act failed during the measurement: " + firstFailure);
                    }
                }
            }

            var sw = Stopwatch.StartNew();
            if (parallel)
            {
                await Task.WhenAll(
                    LoopAsync(refA, () => genA, g => genA = g),
                    LoopAsync(refB, () => genB, g => genB = g));
            }
            else
            {
                await LoopAsync(refA, () => genA, g => genA = g);
                await LoopAsync(refB, () => genB, g => genB = g);
            }

            return (sw.Elapsed.TotalMilliseconds, ok);
        }

        await RunAsync(parallel: false);   // warm-up
        var serial = await RunAsync(parallel: false);
        var par = await RunAsync(parallel: true);

        var repoRoot = AppContext.BaseDirectory;
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot, "OmniWin.sln"))) repoRoot = Directory.GetParent(repoRoot)?.FullName;
        var outPath = Path.Combine(repoRoot!, "artifacts", "benchmarks", "ws2-browser-parallel-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var doc = new
        {
            measured_utc = DateTimeOffset.UtcNow,
            page_a = "elements150.html (150 elements)",
            page_b = "large.html (201 elements)",
            acts_per_tab = actsPerTab,
            serial_total_ms = Math.Round(serial.Ms, 1),
            serial_ok = serial.Ok,
            parallel_total_ms = Math.Round(par.Ms, 1),
            parallel_ok = par.Ok,
            speedup = Math.Round(serial.Ms / par.Ms, 2),
            note = "click acts, each followed by the 150-element verify scan; headless Edge; same machine, same run"
        };
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);

        Assert.Equal(actsPerTab * 2, serial.Ok);
        Assert.Equal(actsPerTab * 2, par.Ok);
    }


    public static bool BenchEnabled => Environment.GetEnvironmentVariable("OMNIWIN_PARALLEL_BENCH") == "1";

    private static string FindWebFixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "testapps", "web-fixtures")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        return dir is not null ? Path.Combine(dir, "testapps", "web-fixtures") : throw new DirectoryNotFoundException("Could not find testapps/web-fixtures");
    }
}
