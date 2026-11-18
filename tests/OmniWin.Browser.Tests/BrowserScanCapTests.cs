using System.Diagnostics;
using System.Text.Json;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser.Tests;

/// <summary>
/// WS4 finding 6: one scan inspects at most 3000 candidate elements, so a 10,000-button page answers well inside the tool
/// timeout, and what was not read is reported (never silent). Pages under the cap behave exactly as before.
/// </summary>
public sealed class BrowserScanCapTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private LocalFixtureServer _server = null!;
    private BrowserEngine _engine = null!;

    public async ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _server = new LocalFixtureServer(FindWebFixturesDir());
        _server.Start();

        var config = new OmniWinConfig { Browser = new BrowserConfig { Mode = "dedicated", ProfileDir = Path.Combine(_tempDir, "browser-profile") } };
        var kill = new TestKillSwitch();
        _engine = new BrowserEngine(config, OmniWinPaths.FromOverride(_tempDir), new SafetyGate(config, kill), kill, new TestUserActivity(), new TestMetrics());
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
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* temp folder cleanup is best effort */ }
    }

    [Fact(Timeout = 120000)]
    public async Task A_10000_button_page_answers_quickly_and_says_how_many_buttons_were_not_read()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "biglist10k.html"), ct);

        var sw = Stopwatch.StartNew();
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(MaxElements: 1000), ct);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"observe took {sw.Elapsed.TotalSeconds:F1} s");
        Assert.Contains("beyond the scan limit", obs.Text);
        Assert.Contains("more not read (page scan limit", obs.Text);
        Assert.Contains("button \"Row 1\"", obs.Text);        // the first rows are listed
        Assert.DoesNotContain("Row 10000", obs.Text);          // the last row is past the cap and is not pretended to be there
    }

    [Fact(Timeout = 120000)]
    public async Task A_2000_button_page_is_under_the_cap_and_shows_no_scan_limit_line()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "biglist.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(MaxElements: 1000), ct);

        Assert.DoesNotContain("beyond the scan limit", obs.Text);
        Assert.DoesNotContain("page scan limit", obs.Text);
    }

    [Fact(Timeout = 180000)]
    public async Task Measure_2000_and_10000_button_observe_and_write_the_artifact()
    {
        var ct = TestContext.Current.CancellationToken;
        var rows = new Dictionary<string, object>();
        foreach (var (key, page) in new[] { ("biglist_2000", "biglist.html"), ("biglist_10000", "biglist10k.html") })
        {
            await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + page), ct);
            var samples = new List<double>();
            var text = "";
            for (var i = 0; i < 10; i++)
            {
                var sw = Stopwatch.StartNew();
                var o = await _engine.ObserveAsync(new BrowserObserveRequest(MaxElements: 1000), ct);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                text = o.Text;
            }

            samples.Sort();
            rows[key] = new
            {
                page,
                samples = samples.Count,
                p50_ms = Math.Round(samples[samples.Count / 2], 1),
                max_ms = Math.Round(samples[^1], 1),
                capped_line_present = text.Contains("beyond the scan limit", StringComparison.Ordinal),
                response_chars = text.Length,
            };
        }

        var dir = Path.Combine(FindRepoRoot(), "artifacts", "benchmarks");
        Directory.CreateDirectory(dir);
        var doc = new { measured_utc = DateTimeOffset.UtcNow, note = "browser_observe max_elements=1000, headless Edge, per-scan cap 3000 candidates", rows };
        await File.WriteAllTextAsync(Path.Combine(dir, "ws2-observe-scan-cap-v1.json"), JsonSerializer.Serialize(doc, CoreJson.Indented), ct);
        Assert.True(((dynamic)rows["biglist_10000"]).max_ms < 20000);
    }

    private static string FindWebFixturesDir() => Path.Combine(FindRepoRoot(), "testapps", "web-fixtures");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OmniWin.sln"))) dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find OmniWin.sln");
    }
}
