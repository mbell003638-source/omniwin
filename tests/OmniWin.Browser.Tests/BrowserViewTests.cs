using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser.Tests;

/// <summary>
/// <c>browser_view</c>: a look-only, masked screenshot of a tab. Headless, on local fixture pages only; NavigationGuard is
/// installed on every connection. Pixels are inspected in memory and never written anywhere.
/// </summary>
// Same collection as BrowserEngineTests: the two classes each start Edge and a fixture server, and run one after the other
// (run side by side they slowed the tab-window test past its time limit).
[Collection("Browser engine")]
public sealed class BrowserViewTests : IAsyncLifetime
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

        var config = new OmniWinConfig
        {
            Browser = new BrowserConfig { Mode = "dedicated", ProfileDir = Path.Combine(_tempDir, "browser-profile") }
        };
        _killSwitch = new TestKillSwitch();
        _engine = new BrowserEngine(config, OmniWinPaths.FromOverride(_tempDir), new SafetyGate(config, _killSwitch),
            _killSwitch, new TestUserActivity(), new TestMetrics());

        // Fail closed: every CDP connection gets NavigationGuard before its first command.
        _engine.ConnectionCreated = connection => connection.CommandInterceptor = NavigationGuard.Check;
        await _engine.EnsureConnectedAsync(headless: true, TestContext.Current.CancellationToken);
        Assert.NotNull(_engine.Connection!.CommandInterceptor);
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
            // Ignore temp folder cleanup errors
        }
    }

    private CdpSession ActiveSession() =>
        (CdpSession)_engine.GetType()
            .GetMethod("GetActiveSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null)!;

    private Task Open(string page, CancellationToken ct) =>
        _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + page), ct);

    /// <summary>The CSS rectangle of an element, the page's viewport width and the device pixel ratio.</summary>
    private async Task<(double X, double Y, double W, double H, double ViewportWidth)> RectOf(string id, CancellationToken ct)
    {
        var raw = await ActiveSession().EvaluatePageScriptAsync(
            $"(function(){{var r=document.getElementById('{id}').getBoundingClientRect();return [r.left,r.top,r.width,r.height,window.innerWidth].join(',');}})()", ct);
        var p = raw.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return (p[0], p[1], p[2], p[3], p[4]);
    }

    /// <summary>Samples the inside of a CSS rectangle in the returned image. Returns (samples, darkSamples).</summary>
    private static async Task<(int Samples, int Dark)> SampleAsync(ViewImage image, (double X, double Y, double W, double H, double ViewportWidth) rect, CancellationToken ct)
    {
        var (width, height, bgra) = await ViewImaging.DecodeAsync(image.Data, ct);
        var scale = width / rect.ViewportWidth;
        var samples = 0;
        var dark = 0;
        for (var y = (rect.Y * scale) + 4; y < ((rect.Y + rect.H) * scale) - 4; y += 3)
        {
            for (var x = (rect.X * scale) + 4; x < ((rect.X + rect.W) * scale) - 4; x += 5)
            {
                if ((int)x >= width || (int)y >= height) continue;
                var o = (((int)y * width) + (int)x) * 4;
                samples++;
                if (bgra[o] <= 12 && bgra[o + 1] <= 12 && bgra[o + 2] <= 12) dark++;
            }
        }

        return (samples, dark);
    }

    [Fact(Timeout = 90000)]
    public async Task A_view_returns_an_image_with_the_untrusted_header_and_a_metadata_line()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("controls.html", ct);

        var view = await _engine.ViewTabAsync(null, false, null, ct);

        Assert.True(view.Ok, $"{view.Failure?.Status}/{view.Failure?.Reason}/{view.Failure?.Detail}");
        Assert.Contains(view.Image!.MediaType, new[] { "image/png", "image/jpeg" });
        Assert.True(view.Image.Data.Length > 100);

        var text = ViewFormat.Render(view);
        var lines = text.Split('\n');
        Assert.Equal(ResultFormatter.UntrustedHeader, lines[0]);
        Assert.StartsWith("view target=t", lines[1]);
        Assert.Contains("title=\"Controls Test Page\"", lines[1]);
        Assert.Contains(" captured=", lines[1]);
        Assert.Contains(" scale=", lines[1]);
        Assert.True(text.Length < 400, "the text line holds metadata only, never image data");
    }

    [Fact(Timeout = 90000)]
    public async Task Max_side_is_respected_defaults_to_1280_and_never_exceeds_1920()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("controls.html", ct);

        var small = await _engine.ViewTabAsync(null, false, 300, ct);
        Assert.True(small.Ok);
        Assert.True(Math.Max(small.Image!.Width, small.Image.Height) <= 300, $"{small.Image.Width}x{small.Image.Height}");

        var byDefault = await _engine.ViewTabAsync(null, false, null, ct);
        Assert.True(byDefault.Ok);
        Assert.True(Math.Max(byDefault.Image!.Width, byDefault.Image.Height) <= 1280);

        var huge = await _engine.ViewTabAsync(null, false, 5000, ct);
        Assert.True(huge.Ok);
        Assert.True(Math.Max(huge.Image!.Width, huge.Image.Height) <= 1920);
    }

    [Fact(Timeout = 90000)]
    public async Task Password_and_payment_fields_are_uniform_black_and_ordinary_fields_are_not()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("controls.html", ct);
        var view = await _engine.ViewTabAsync(null, false, 1920, ct);
        Assert.True(view.Ok);

        foreach (var id in new[] { "input-password", "input-cc" })
        {
            var rect = await RectOf(id, ct);
            Assert.True(rect.W > 20 && rect.H > 10 && rect.Y + rect.H < 780, $"{id} must be on screen for this test");
            var (samples, dark) = await SampleAsync(view.Image!, rect, ct);
            Assert.True(samples > 10);
            Assert.Equal(samples, dark);   // every sampled pixel inside the field is black
        }

        var ordinary = await SampleAsync(view.Image!, await RectOf("input-text", ct), ct);
        Assert.True(ordinary.Dark < ordinary.Samples, "an ordinary text field must stay visible");
    }

    [Fact(Timeout = 90000)]
    public async Task A_cross_origin_frame_is_blacked_out_whole_and_a_same_origin_frame_is_not()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("frames-cross.html", ct);
        await Task.Delay(700, ct);
        var cross = await _engine.ViewTabAsync(null, false, 1920, ct);
        Assert.True(cross.Ok);
        var (crossSamples, crossDark) = await SampleAsync(cross.Image!, await RectOf("other", ct), ct);
        Assert.True(crossSamples > 10);
        Assert.Equal(crossSamples, crossDark);

        await Open("frames.html", ct);
        await Task.Delay(700, ct);
        var same = await _engine.ViewTabAsync(null, false, 1920, ct);
        Assert.True(same.Ok);
        var (sameSamples, sameDark) = await SampleAsync(same.Image!, await RectOf("inner", ct), ct);
        Assert.True(sameDark < sameSamples, "a same-origin frame without sensitive fields stays visible");
    }

    [Fact(Timeout = 90000)]
    public async Task A_full_page_view_covers_the_whole_page_and_masks_a_password_field_below_the_fold()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("tall.html", ct);

        var view = await _engine.ViewTabAsync(null, true, 1920, ct);

        Assert.True(view.Ok, $"{view.Failure?.Reason}/{view.Failure?.Detail}");
        Assert.True(view.Image!.SourceHeight > 2400, $"source height {view.Image.SourceHeight}");

        // The field is far below the first screen: its rectangle is in page coordinates.
        var raw = await ActiveSession().EvaluatePageScriptAsync(
            "(function(){var r=document.getElementById('deep-password').getBoundingClientRect();return [r.left,r.top+window.scrollY,r.width,r.height].join(',');})()", ct);
        var p = raw.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var (width, height, bgra) = await ViewImaging.DecodeAsync(view.Image.Data, ct);
        var scale = width / (double)view.Image.SourceWidth;
        var samples = 0;
        var dark = 0;
        for (var y = (p[1] * scale) + 4; y < ((p[1] + p[3]) * scale) - 4; y += 3)
        {
            for (var x = (p[0] * scale) + 4; x < ((p[0] + p[2]) * scale) - 4; x += 5)
            {
                var o = (((int)y * width) + (int)x) * 4;
                samples++;
                if (bgra[o] <= 12 && bgra[o + 1] <= 12 && bgra[o + 2] <= 12) dark++;
            }
        }

        Assert.True(samples > 10);
        Assert.Equal(samples, dark);
    }

    [Fact(Timeout = 90000)]
    public async Task If_the_sensitive_fields_cannot_be_read_there_is_no_image()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("controls.html", ct);
        var conn = _engine.Connection!;
        conn.CommandInterceptor = NavigationGuard.With((method, parameters) =>
            method == "Runtime.evaluate" && System.Text.Json.JsonSerializer.Serialize(parameters).Contains("sensitiveRects")
                ? throw new InvalidOperationException("Simulated: the page script did not answer")
                : Task.CompletedTask);

        ViewOutcome view;
        try
        {
            view = await _engine.ViewTabAsync(null, false, null, ct);
        }
        finally
        {
            conn.CommandInterceptor = NavigationGuard.Check;
        }

        Assert.False(view.Ok);
        Assert.Null(view.Image);
        Assert.Equal(ActStatus.Error, view.Failure!.Status);
        Assert.False(view.Failure.InputSent);
    }

    [Fact(Timeout = 90000)]
    public async Task An_open_dialog_blocks_the_view_and_a_tripped_stop_key_denies_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("js-dialog.html", ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var alertRef = obs.Text.Split('\n').First(l => l.Contains("Show alert")).Trim().Split(' ')[0];
        await _engine.ActAsync(new BrowserActRequest(alertRef, obs.Generation, "click"), ct);

        var blocked = await _engine.ViewTabAsync(null, false, null, ct);
        Assert.Null(blocked.Image);
        Assert.Equal(ReasonCodes.ModalBlocking, blocked.Failure!.Reason);
        await _engine.NavigateAsync(new BrowserNavRequest("dialog_dismiss"), ct);

        _killSwitch.IsTripped = true;
        var denied = await _engine.ViewTabAsync(null, false, null, ct);
        Assert.Null(denied.Image);
        Assert.Equal(ActStatus.Denied, denied.Failure!.Status);
        Assert.Equal(ReasonCodes.KillSwitch, denied.Failure.Reason);
    }

    [Fact(Timeout = 90000)]
    public async Task A_view_sends_no_input_and_leaves_the_page_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("controls.html", ct);
        var before = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent + '|' + document.activeElement.tagName", ct);

        await _engine.ViewTabAsync(null, false, null, ct);

        var after = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent + '|' + document.activeElement.tagName", ct);
        Assert.Equal(before, after);
    }

    /// <summary>The CSS rectangle (page viewport coordinates) that a JavaScript expression returns as "x,y,w,h".</summary>
    private async Task<(double X, double Y, double W, double H, double ViewportWidth)> RectBy(string rectExpression, CancellationToken ct)
    {
        var raw = await ActiveSession().EvaluatePageScriptAsync(
            $"(function(){{var r=({rectExpression});return [r.left,r.top,r.width,r.height,window.innerWidth].join(',');}})()", ct);
        var p = raw.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return (p[0], p[1], p[2], p[3], p[4]);
    }

    [Fact(Timeout = 90000)]
    public async Task A_password_field_inside_an_open_shadow_root_is_black_and_a_closed_shadow_root_with_an_input_is_black_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("shadow.html", ct);
        var view = await _engine.ViewTabAsync(null, false, 1920, ct);
        Assert.True(view.Ok, $"{view.Failure?.Reason}/{view.Failure?.Detail}");

        var open = await RectBy("document.getElementById('open-host').shadowRoot.querySelector('input').getBoundingClientRect()", ct);
        var (openSamples, openDark) = await SampleAsync(view.Image!, open, ct);
        Assert.True(openSamples > 10);
        Assert.Equal(openSamples, openDark);

        // The closed root's host: its whole box is black (the field inside can't be told from the rest).
        var closed = await RectBy("document.getElementById('closed-host').getBoundingClientRect()", ct);
        var (closedSamples, closedDark) = await SampleAsync(view.Image!, closed, ct);
        Assert.True(closedSamples > 10);
        Assert.Equal(closedSamples, closedDark);
    }

    [Fact(Timeout = 90000)]
    public async Task A_frame_two_levels_deep_is_black_whole_even_though_it_holds_a_password_field()
    {
        var ct = TestContext.Current.CancellationToken;
        await Open("frames-deep.html", ct);
        await Task.Delay(900, ct);   // both frames load asynchronously
        var view = await _engine.ViewTabAsync(null, false, 1920, ct);
        Assert.True(view.Ok, $"{view.Failure?.Reason}/{view.Failure?.Detail}");

        // The deep frame's rectangle in page coordinates: the mid frame's origin plus the deep frame's place inside it.
        var mid = await RectBy("document.getElementById('mid').getBoundingClientRect()", ct);
        var deepInMid = await RectBy("document.getElementById('mid').contentDocument.getElementById('deep').getBoundingClientRect()", ct);
        var deep = (X: mid.X + deepInMid.X, Y: mid.Y + deepInMid.Y, W: deepInMid.W, H: deepInMid.H, ViewportWidth: mid.ViewportWidth);

        var (samples, dark) = await SampleAsync(view.Image!, deep, ct);
        Assert.True(samples > 10);
        Assert.Equal(samples, dark);

        // The control: the mid frame's own top area (outside the deep frame) is not black.
        var control = await SampleAsync(view.Image!, (mid.X, mid.Y + 2, mid.W, 14, mid.ViewportWidth), ct);
        Assert.True(control.Dark < control.Samples, "the mid frame outside the deep frame stays visible");
    }

    public static bool BenchEnabled => Environment.GetEnvironmentVariable("OMNIWIN_VIEW_BENCH") == "1";

    /// <summary>browser_view timing and size (report-only row), opt-in: OMNIWIN_VIEW_BENCH=1. Headless, local fixtures, no input.</summary>
    [Fact(Skip = "Set OMNIWIN_VIEW_BENCH=1 to measure.", SkipUnless = nameof(BenchEnabled), Timeout = 300000)]
    public async Task Measure_browser_view()
    {
        var ct = TestContext.Current.CancellationToken;
        var rows = new Dictionary<string, object>();
        foreach (var (key, page, full) in new[] { ("controls_viewport", "controls.html", false), ("large_viewport", "large.html", false), ("tall_full_page", "tall.html", true) })
        {
            await Open(page, ct);
            ViewOutcome? last = null;
            for (var i = 0; i < 5; i++) last = await _engine.ViewTabAsync(null, full, null, ct);
            var samples = new List<double>();
            var statuses = new List<string>();
            for (var i = 0; i < 50; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                last = await _engine.ViewTabAsync(null, full, null, ct);
                samples.Add(sw.Elapsed.TotalMilliseconds);
                statuses.Add(last.Ok ? "ok" : $"{last.Failure!.Status}/{last.Failure.Reason}");
            }

            var sorted = samples.Order().ToList();
            double At(double q) => Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))], 2);
            rows[key] = new
            {
                page,
                full_page = full,
                samples = sorted.Count,
                status_counts = statuses.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()),
                p50_ms = At(0.5),
                p95_ms = At(0.95),
                p99_ms = At(0.99),
                output = last!.Ok ? $"{last.Image!.Width}x{last.Image.Height} {last.Image.MediaType}" : "none",
                output_kb = last.Ok ? Math.Round(last.Image!.Data.Length / 1024.0, 1) : 0,
            };
        }

        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OmniWin.sln"))) dir = Directory.GetParent(dir)?.FullName;
        var path = Path.Combine(dir!, "artifacts", "benchmarks", "view-browser-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { started_utc = DateTimeOffset.UtcNow, rows }, CoreJson.Indented), ct);
    }

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
