using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser.Tests;

[Collection("Browser engine")]
public sealed class BrowserEngineTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private LocalFixtureServer _server = null!;
    private BrowserEngine _engine = null!;
    private TestKillSwitch _killSwitch = null!;
    private TestUserActivity _userActivity = null!;
    private TestMetrics _metrics = null!;

    public async ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        var fixturesDir = FindWebFixturesDir();
        _server = new LocalFixtureServer(fixturesDir);
        _server.Start();

        var paths = OmniWinPaths.FromOverride(_tempDir);
        var config = new OmniWinConfig
        {
            Browser = new BrowserConfig
            {
                Mode = "dedicated",
                ProfileDir = Path.Combine(_tempDir, "browser-profile")
            }
        };

        _killSwitch = new TestKillSwitch();
        _userActivity = new TestUserActivity();
        _metrics = new TestMetrics();

        var gate = new SafetyGate(config, _killSwitch);
        _engine = new BrowserEngine(config, paths, gate, _killSwitch, _userActivity, _metrics);

        // Fail closed: every CDP connection, including one made by a reconnect, gets NavigationGuard before its
        // first command, so no test can navigate to a non-local URL.
        _engine.ConnectionCreated = connection => connection.CommandInterceptor = NavigationGuard.Check;

        // Ensure launched in headless mode for safe test execution
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
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp folder cleanup errors
        }
    }

    [Fact]
    public async Task ListTabs_returns_tabs_with_monotonic_tN_ids()
    {
        var tabs = await _engine.ListTabsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(tabs);
        Assert.StartsWith("t", tabs[0].Id);
    }

    [Fact]
    public async Task Navigate_and_Observe_formats_untrusted_header_and_redacts_passwords_and_payment()
    {
        var ct = TestContext.Current.CancellationToken;
        var navRes = await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        Assert.Equal(ActStatus.Unverified, navRes.Status);
        Assert.True(navRes.InputSent);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        Assert.False(obs.IsDiff);
        Assert.Contains(ResultFormatter.UntrustedHeader, obs.Text);
        Assert.Contains("Controls Test Page", obs.Text);
        Assert.Contains("value=<redacted>", obs.Text); // Password and CC field redaction
        Assert.Contains("[click]", obs.Text);
        Assert.Contains("[type,set_value,clear]", obs.Text);
        Assert.Contains("omitted:", obs.Text);
    }

    [Fact]
    public async Task Observe_large_dom_shows_truncation_footer()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "large.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(MaxElements: 50), ct);
        Assert.Contains("truncated:", obs.Text);
        Assert.Contains("more (raise max_elements or narrow scope)", obs.Text);
    }

    [Fact]
    public async Task Delete_actions_are_denied_with_input_sent_false_and_empty_page_event_log()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "delete.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);

        // Find delete button
        var lines = obs.Text.Split('\n');
        var deleteBtnLine = lines.FirstOrDefault(l => l.Contains("Delete account"));
        Assert.NotNull(deleteBtnLine);

        var refId = deleteBtnLine.Trim().Split(' ')[0];
        Assert.StartsWith("b", refId);

        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "click"), ct);

        // Must be denied by policy forbidden, and input_sent must strictly be false!
        Assert.Equal(ActStatus.Denied, actRes.Status);
        Assert.False(actRes.InputSent);
        Assert.Equal(ReasonCodes.PolicyForbidden, actRes.Reason);

        // Verify page event log remains empty
        var session = (CdpSession)_engine.GetType()
            .GetMethod("GetActiveSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null)!;
        var logContent = await session.EvaluateRawAsync("document.getElementById('event-log').textContent", ct);
        Assert.True(string.IsNullOrWhiteSpace(logContent), $"Expected event log to be empty, but got: {logContent}");
    }

    [Fact]
    public async Task Act_click_sends_input_and_records_in_event_log()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var clickMeLine = obs.Text.Split('\n').First(l => l.Contains("Click Me"));
        var refId = clickMeLine.Trim().Split(' ')[0];

        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "click"), ct);
        Assert.True(actRes.InputSent);
        Assert.Equal(Channels.CdpMouse, actRes.Channel);

        var session = (CdpSession)_engine.GetType()
            .GetMethod("GetActiveSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null)!;
        var logContent = await session.EvaluateRawAsync("document.getElementById('event-log').textContent", ct);
        Assert.Contains("click:btn-click-me", logContent);
    }

    [Fact]
    public async Task Act_set_value_updates_value_and_confirms()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var textLine = obs.Text.Split('\n').First(l => l.Contains("initial text"));
        var refId = textLine.Trim().Split(' ')[0];

        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "set_value", "updated hello"), ct);
        Assert.Equal(ActStatus.Confirmed, actRes.Status);
        Assert.True(actRes.InputSent);
        Assert.Equal(Channels.SetValue, actRes.Channel);

        var session = (CdpSession)_engine.GetType()
            .GetMethod("GetActiveSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null)!;
        var currentVal = await session.EvaluateRawAsync("document.getElementById('input-text').value", ct);
        Assert.Equal("updated hello", currentVal);
    }

    [Fact]
    public async Task Act_precheck_blocks_disabled_element()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var disabledLine = obs.Text.Split('\n').First(l => l.Contains("Disabled Button"));
        var refId = disabledLine.Trim().Split(' ')[0];

        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "click"), ct);
        Assert.Equal(ActStatus.Blocked, actRes.Status);
        Assert.False(actRes.InputSent);
        Assert.Equal(ReasonCodes.Disabled, actRes.Reason);
    }

    [Fact]
    public async Task Act_precheck_blocks_occluded_element()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "dialogs.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var coveredLine = obs.Text.Split('\n').First(l => l.Contains("Covered Target"));
        var refId = coveredLine.Trim().Split(' ')[0];

        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "click"), ct);
        Assert.Equal(ActStatus.Blocked, actRes.Status);
        Assert.False(actRes.InputSent);
        Assert.Equal(ReasonCodes.OccludedSameWindow, actRes.Reason);
    }

    [Fact]
    public async Task Diff_reports_added_modified_and_removed_elements()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "dynamic.html"), ct);

        var obs1 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var addBtnLine = obs1.Text.Split('\n').First(l => l.Contains("Add Element"));
        var addRefId = addBtnLine.Trim().Split(' ')[0];

        // Click Add Element to trigger DOM mutation
        await _engine.ActAsync(new BrowserActRequest(addRefId, obs1.Generation, "click"), ct);

        // Request diff since generation 1
        var diffObs = await _engine.ObserveAsync(new BrowserObserveRequest(SinceGeneration: obs1.Generation), ct);
        Assert.True(diffObs.IsDiff);
        Assert.Contains("+ ", diffObs.Text);
        Assert.Contains("Newly Added", diffObs.Text);
    }

    [Fact]
    public async Task DescribeAsync_and_ResolveSelectorAsync_work()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var clickMeLine = obs.Text.Split('\n').First(l => l.Contains("Click Me"));
        var refId = clickMeLine.Trim().Split(' ')[0];

        var desc = await _engine.DescribeAsync(refId, ct);
        Assert.NotNull(desc);
        Assert.Equal("browser", desc.Engine);
        Assert.Equal("button", desc.Role);
        Assert.Equal("Click Me", desc.Name);

        var resolved = await _engine.ResolveSelectorAsync("controls.html", desc.Path, ct);
        Assert.NotNull(resolved);
        Assert.Equal(refId, resolved.Ref);

        // The resolve recorded its scan, so the resolved ref and generation can be acted on without observing first.
        var res = await _engine.ActAsync(new BrowserActRequest(resolved.Ref, resolved.Generation, "click"), ct);
        Assert.True(res.InputSent);
        Assert.NotEqual(ActStatus.Stale, res.Status);
    }

    [Fact]
    public async Task Spoof_page_cannot_fake_omniwin_indexer_or_bypass_delete_denial()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "spoof.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);

        // OmniWin MUST see the real button name "Delete account", NOT the spoofed name "Save"
        Assert.DoesNotContain("Save", obs.Text);
        Assert.Contains("Delete account", obs.Text);

        var lines = obs.Text.Split('\n');
        var deleteLine = lines.First(l => l.Contains("Delete account"));
        var refId = deleteLine.Trim().Split(' ')[0];

        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "click"), ct);
        Assert.Equal(ActStatus.Denied, actRes.Status);
        Assert.False(actRes.InputSent);
        Assert.Equal(ReasonCodes.PolicyForbidden, actRes.Reason);

        // Nothing reached the page: its event log is still empty.
        var logContent = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.True(string.IsNullOrWhiteSpace(logContent), $"Expected event log to be empty, but got: {logContent}");
    }

    [Fact]
    public async Task Isolated_world_is_recreated_after_navigation_to_second_page()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs1 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        Assert.Contains("Controls Test Page", obs1.Text);

        // Navigate to second page
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "dynamic.html"), ct);
        var obs2 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        Assert.Contains("Dynamic Elements Test Page", obs2.Text);

        var addBtnLine = obs2.Text.Split('\n').First(l => l.Contains("Add Element"));
        var refId = addBtnLine.Trim().Split(' ')[0];

        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs2.Generation, "click"), ct);
        Assert.True(actRes.InputSent);
    }

    [Fact]
    public async Task Target_swap_before_click_never_clicks_replaced_element()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "moving.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var harmlessLine = obs.Text.Split('\n').First(l => l.Contains("Click me"));
        var refId = harmlessLine.Trim().Split(' ')[0];

        // Trigger swap on page: Delete button moves under harmless button's old position
        var session = (CdpSession)_engine.GetType()
            .GetMethod("GetActiveSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null)!;
        await session.EvaluatePageScriptAsync("window.swapElements()", ct);

        // Click old refId
        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "click"), ct);

        // Under S2, this must NOT reach the Delete button!
        var logContent = await session.EvaluateRawAsync("document.getElementById('event-log').textContent", ct);
        Assert.DoesNotContain("click:delete", logContent);
        Assert.False(actRes.InputSent);
    }

    [Fact]
    public async Task Act_with_older_generation_when_element_changed_returns_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs1 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var submitLine = obs1.Text.Split('\n').First(l => l.Contains("Submit"));
        var refId = submitLine.Trim().Split(' ')[0];

        // Change button text and advance generation
        var session = (CdpSession)_engine.GetType()
            .GetMethod("GetActiveSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null)!;
        await session.EvaluatePageScriptAsync("document.getElementById('btn-submit').textContent = 'Submitting...';", ct);

        // Advance generation via another observe
        var obs2 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        Assert.True(obs2.Generation > obs1.Generation);

        // Act with old generation obs1.Generation
        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs1.Generation, "click"), ct);
        Assert.Equal(ActStatus.Stale, actRes.Status);
        Assert.False(actRes.InputSent);
    }

    [Fact]
    public async Task Act_with_older_generation_when_element_unchanged_proceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs1 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var inputLine = obs1.Text.Split('\n').First(l => l.Contains("initial text"));
        var refId = inputLine.Trim().Split(' ')[0];

        // Advance generation via another observe without changing input-text
        var obs2 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        Assert.True(obs2.Generation > obs1.Generation);

        // Act with old generation obs1.Generation on unchanged text input
        var actRes = await _engine.ActAsync(new BrowserActRequest(refId, obs1.Generation, "set_value", "new text"), ct);
        Assert.Equal(ActStatus.Confirmed, actRes.Status);
        Assert.True(actRes.InputSent);
    }

    [Theory]
    [InlineData("navigate")]
    [InlineData("reload")]
    [InlineData("back")]
    [InlineData("forward")]
    [InlineData("new_tab")]
    [InlineData("switch_tab")]
    [InlineData("close_tab")]
    public async Task NavigateAsync_when_kill_switch_tripped_returns_denied_and_does_not_send_input(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        _killSwitch.IsTripped = true;

        var req = new BrowserNavRequest(action, action is "navigate" or "new_tab" ? _server.BaseUrl + "delete.html" : null);
        var res = await _engine.NavigateAsync(req, ct);

        Assert.Equal(ActStatus.Denied, res.Status);
        Assert.Equal(ReasonCodes.KillSwitch, res.Reason);
        Assert.False(res.InputSent);
    }

    [Fact]
    public async Task NavigateAsync_when_user_active_recently_returns_blocked_and_does_not_send_input()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        _userActivity.UserActiveRecently = true;

        var res = await _engine.NavigateAsync(new BrowserNavRequest("reload"), ct);
        Assert.Equal(ActStatus.Blocked, res.Status);
        Assert.Equal(ReasonCodes.UserActive, res.Reason);
        Assert.False(res.InputSent);
    }

    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>hi</b>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("blob:http://localhost/test")]
    [InlineData("view-source:http://127.0.0.1/")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("search-ms:query=test")]
    public void ClassifyNavRisk_marks_non_web_urls_risky_and_never_navigates(string url)
    {
        // A pure function on a string: nothing is navigated, opened or refused. The label only records.
        Assert.Equal(RiskClass.Risky, BrowserEngine.ClassifyNavRisk("navigate", url));
    }

    [Fact]
    public async Task NavigateAsync_close_tab_is_allowed_and_classified_risky()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(RiskClass.Risky, BrowserEngine.ClassifyNavRisk("close_tab", null));

        // Create a new tab first so closing doesn't close the only tab
        await _engine.NavigateAsync(new BrowserNavRequest("new_tab", "about:blank"), ct);
        var res = await _engine.NavigateAsync(new BrowserNavRequest("close_tab"), ct);
        Assert.Equal(ActStatus.Unverified, res.Status);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task NavigateAsync_http_allowed_and_classified_normal()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = _server.BaseUrl + "controls.html";
        Assert.Equal(RiskClass.Normal, BrowserEngine.ClassifyNavRisk("navigate", url));

        var res = await _engine.NavigateAsync(new BrowserNavRequest("navigate", url), ct);
        Assert.Equal(ActStatus.Unverified, res.Status);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task NavigateAsync_without_expectation_returns_unverified_with_input_sent_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = _server.BaseUrl + "controls.html";
        var res = await _engine.NavigateAsync(new BrowserNavRequest("navigate", url), ct);
        Assert.Equal(ActStatus.Unverified, res.Status);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task ActAsync_stop_after_focus_in_type_reports_error_with_input_interrupted_and_input_sent_true()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var inputLine = obs.Text.Split('\n').First(l => l.Contains("initial text"));
        var refId = inputLine.Trim().Split(' ')[0];

        // Trip the kill switch on the check immediately after focus
        var checkCount = 0;
        _killSwitch.IsTrippedCustom = () =>
        {
            checkCount++;
            // Check 1: SafetyGate.AuthorizeAsync
            // Check 2: BrowserEngine.ActAsync line before input phase
            // Check 3: Inside type after focus has been sent
            return checkCount >= 3;
        };

        var res = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "type", "hello"), ct);
        _killSwitch.IsTrippedCustom = null;

        Assert.Equal(ActStatus.Error, res.Status);
        Assert.Equal(ReasonCodes.InputInterrupted, res.Reason);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task ActAsync_failure_between_mouse_pressed_and_released_reports_error_with_input_interrupted_and_input_sent_true()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var btnLine = obs.Text.Split('\n').First(l => l.Contains("Click Me"));
        var refId = btnLine.Trim().Split(' ')[0];

        // Intercept CDP command to fail on mouseReleased
        var conn = _engine.Connection!;
        conn.CommandInterceptor = NavigationGuard.With((method, parameters) =>
        {
            if (method == "Input.dispatchMouseEvent")
            {
                var json = System.Text.Json.JsonSerializer.Serialize(parameters);
                if (json.Contains("mouseReleased"))
                {
                    throw new InvalidOperationException("Simulated socket failure on mouseReleased");
                }
            }
            return Task.CompletedTask;
        });

        var res = await _engine.ActAsync(new BrowserActRequest(refId, obs.Generation, "click"), ct);
        conn.CommandInterceptor = NavigationGuard.Check;

        Assert.Equal(ActStatus.Error, res.Status);
        Assert.Equal(ReasonCodes.InputInterrupted, res.Reason);
        Assert.True(res.InputSent);
    }

    // Nothing is refused for its scheme (owner decision). Tests therefore open only data: URLs. Never file:, javascript:,
    // ms-settings: or search-ms: in a test run; they can open files, settings or other programs on this machine.
    // NavigationGuard throws before any such URL is sent.
    [Fact]
    public async Task NavigateAsync_data_url_is_not_refused_for_its_scheme()
    {
        var ct = TestContext.Current.CancellationToken;

        var res = await _engine.NavigateAsync(new BrowserNavRequest("navigate", "data:text/html,<title>ok</title><b>hi</b>"), ct);

        Assert.NotEqual(ActStatus.Denied, res.Status);
        Assert.NotEqual(ReasonCodes.PolicyForbidden, res.Reason);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task ActAsync_clicking_a_data_link_is_not_refused_for_its_scheme()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var dataLink = RefOfLine(obs, "Data link");

        var res = await _engine.ActAsync(new BrowserActRequest(dataLink, obs.Generation, "click"), ct);

        Assert.NotEqual(ActStatus.Denied, res.Status);
        Assert.True(res.InputSent);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("press")]
    public void ClassifyActRisk_non_web_link_is_risky_for_click_and_activation_key(string action)
    {
        var link = new LiveElementInspection(true, Role: "link", Value: "file:///anything");
        Assert.Equal(RiskClass.Risky, BrowserEngine.ClassifyActRisk(action, link, action == "press" ? "Enter" : null));

        var web = new LiveElementInspection(true, Role: "link", Value: "http://127.0.0.1/");
        Assert.Equal(RiskClass.Normal, BrowserEngine.ClassifyActRisk(action, web, action == "press" ? "Enter" : null));
    }

    // ---- N3: an act needs an observed baseline ----

    [Fact]
    public async Task Act_with_unknown_generation_returns_stale_and_sends_no_input()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var clickMe = RefOfLine(obs, "Click Me");

        var res = await _engine.ActAsync(new BrowserActRequest(clickMe, obs.Generation + 9999, "click"), ct);

        Assert.Equal(ActStatus.Stale, res.Status);
        Assert.False(res.InputSent);
        var log = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.True(string.IsNullOrWhiteSpace(log), log);
    }

    [Fact]
    public async Task Act_with_ref_that_was_not_in_that_generation_returns_stale_and_sends_no_input()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "dynamic.html"), ct);
        var obs1 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var addRef = RefOfLine(obs1, "Add Element");

        // Adds a new element that gen 1 never contained.
        await _engine.ActAsync(new BrowserActRequest(addRef, obs1.Generation, "click"), ct);
        var obs2 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var newRef = RefOfLine(obs2, "Newly Added");
        Assert.DoesNotContain(newRef + " ", obs1.Text);

        var res = await _engine.ActAsync(new BrowserActRequest(newRef, obs1.Generation, "click"), ct);

        Assert.Equal(ActStatus.Stale, res.Status);
        Assert.False(res.InputSent);
    }

    // ---- N4: a missing isolated world refuses the act; it never falls back to the page world ----

    [Fact]
    public async Task Act_when_isolated_world_cannot_be_created_returns_blocked_and_sends_no_input()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var clickMe = RefOfLine(obs, "Click Me");

        var session = ActiveSession();
        typeof(CdpSession).GetField("_isolatedContextId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(session, null);

        var conn = _engine.Connection!;
        conn.CommandInterceptor = NavigationGuard.With((method, _) =>
            method == "Page.createIsolatedWorld"
                ? throw new InvalidOperationException("Simulated: cannot create isolated world")
                : Task.CompletedTask);
        ActResult res;
        try
        {
            res = await _engine.ActAsync(new BrowserActRequest(clickMe, obs.Generation, "click"), ct);
        }
        finally
        {
            conn.CommandInterceptor = NavigationGuard.Check;
        }

        Assert.Equal(ActStatus.Blocked, res.Status);
        Assert.False(res.InputSent);
        var log = await session.EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.True(string.IsNullOrWhiteSpace(log), log);
    }

    [Fact]
    public async Task Descriptor_process_name_matches_launched_browser_executable()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var firstRef = obs.Text.Split('\n').First(l => l.TrimStart().StartsWith("b")).Trim().Split(' ')[0];

        var desc = await _engine.DescribeAsync(firstRef, ct);
        Assert.NotNull(desc);
        Assert.Equal(_engine.BrowserProcessName, desc.Process);
        Assert.True(desc.Process is "msedge.exe" or "chrome.exe", $"Expected msedge.exe or chrome.exe, got {desc.Process}");
    }

    [Fact]
    public async Task Typing_into_field_with_existing_text_computes_expected_value_and_confirms()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var obs1 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var inputLine = obs1.Text.Split('\n').First(l => l.Contains("input") || l.Contains("text"));
        var refId = inputLine.Trim().Split(' ')[0];

        // First set value to "Hello "
        var setRes = await _engine.ActAsync(new BrowserActRequest(refId, obs1.Generation, "set_value", Value: "Hello "), ct);
        Assert.Equal(ActStatus.Confirmed, setRes.Status);

        // Re-observe to get updated generation
        var obs2 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);

        // Now type "World"
        var typeRes = await _engine.ActAsync(new BrowserActRequest(refId, obs2.Generation, "type", Value: "World"), ct);
        Assert.Equal(ActStatus.Confirmed, typeRes.Status);
        Assert.True(typeRes.InputSent);

        // Verify resulting value contains both
        var obs3 = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        Assert.Contains("Hello World", obs3.Text);
    }

    [Fact]
    public async Task Navigation_and_reload_waits_end_on_a_page_load_event_not_on_the_readystate_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var navRes = await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        Assert.True(navRes.InputSent);
        Assert.Contains(ActiveSession().LastNavigationSignal, new[] { "Page.loadEventFired", "Page.domContentEventFired", "Page.navigatedWithinDocument" });

        var reloadRes = await _engine.NavigateAsync(new BrowserNavRequest("reload"), ct);
        Assert.True(reloadRes.InputSent);
        Assert.Contains(ActiveSession().LastNavigationSignal, new[] { "Page.loadEventFired", "Page.domContentEventFired", "Page.navigatedWithinDocument" });

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        Assert.True(obs.ElementCount > 0);
    }

    private CdpSession ActiveSession() =>
        (CdpSession)_engine.GetType()
            .GetMethod("GetActiveSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null)!;

    private static string RefOfLine(Observation obs, string contains) =>
        obs.Text.Split('\n').First(l => l.Contains(contains)).Trim().Split(' ')[0];

    [Fact]
    public async Task Press_goes_to_the_target_ref_not_to_the_element_that_had_focus()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "keys.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var textRef = RefOfLine(obs, "Search box");

        // The page has focus on the Delete button. A Space key press aimed at the text box must not activate it.
        var session = ActiveSession();
        await session.EvaluatePageScriptAsync("document.getElementById('key-delete').focus()", ct);
        Assert.Equal("key-delete", await session.EvaluatePageScriptAsync("document.activeElement.id", ct));

        var res = await _engine.ActAsync(new BrowserActRequest(textRef, obs.Generation, "press", "Space"), ct);

        var log = await session.EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.DoesNotContain("click:delete", log);
        Assert.Contains("keydown:key-text: ", log);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Press_enter_in_text_box_submits_the_form()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "keys.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var textRef = RefOfLine(obs, "Search box");

        var res = await _engine.ActAsync(new BrowserActRequest(textRef, obs.Generation, "press", "Enter"), ct);

        var log = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.Contains("submit:key-form", log);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Type_when_page_moves_focus_away_sends_no_text_and_reports_focus_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "focus-steal.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var fieldA = RefOfLine(obs, "Field A");

        var res = await _engine.ActAsync(new BrowserActRequest(fieldA, obs.Generation, "type", "leaked"), ct);

        var session = ActiveSession();
        var diag = $"{res.Status}/{res.Reason}/{res.Detail}; log={await session.EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct)}; active={await session.EvaluatePageScriptAsync("document.activeElement.id", ct)}";
        Assert.True("" == await session.EvaluatePageScriptAsync("document.getElementById('field-b').value", ct), diag);
        Assert.Equal("", await session.EvaluatePageScriptAsync("document.getElementById('field-a').value", ct));
        Assert.Equal(ActStatus.Error, res.Status);
        Assert.Equal(ReasonCodes.FocusFailed, res.Reason);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Press_when_page_moves_focus_away_sends_no_key_and_reports_focus_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "focus-steal.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var fieldA = RefOfLine(obs, "Field A");

        var session = ActiveSession();
        await session.EvaluatePageScriptAsync(
            "window.__keys = []; document.addEventListener('keydown', e => window.__keys.push(e.key), true)", ct);

        var res = await _engine.ActAsync(new BrowserActRequest(fieldA, obs.Generation, "press", "Enter"), ct);

        Assert.Equal("0", await session.EvaluatePageScriptAsync("window.__keys.length", ct));
        Assert.Equal(ActStatus.Error, res.Status);
        Assert.Equal(ReasonCodes.FocusFailed, res.Reason);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Double_click_fires_a_dblclick_event_without_a_fixed_delay_between_the_clicks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "keys.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var target = RefOfLine(obs, "Double me");

        var res = await _engine.ActAsync(new BrowserActRequest(target, obs.Generation, "double_click"), ct);

        var log = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.Contains("dblclick:key-double", log);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Only_the_last_few_generations_are_kept_so_an_old_generation_is_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var first = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var clickMe = RefOfLine(first, "Click Me");

        for (var i = 0; i < 12; i++)
        {
            await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        }

        var res = await _engine.ActAsync(new BrowserActRequest(clickMe, first.Generation, "click"), ct);

        Assert.Equal(ActStatus.Stale, res.Status);
        Assert.False(res.InputSent);
    }

    [Theory]
    [InlineData("elements150.html", "Btn 1", "Bench input")]
    [InlineData("large.html", "Item #1", "Large input")]
    public async Task Click_set_value_and_type_work_on_big_pages(string page, string buttonName, string inputName)
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + page), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var button = RefOfLine(obs, buttonName);
        var input = RefOfLine(obs, inputName);

        var click = await _engine.ActAsync(new BrowserActRequest(button, obs.Generation, "click"), ct);
        Assert.True(click.InputSent, $"click: {click.Status}/{click.Reason}/{click.Detail}");

        var gen = click.After!.Generation;
        var set = await _engine.ActAsync(new BrowserActRequest(input, gen, "set_value", "abc"), ct);
        Assert.Equal(ActStatus.Confirmed, set.Status);

        gen = set.After!.Generation;
        var typed = await _engine.ActAsync(new BrowserActRequest(input, gen, "type", "d"), ct);
        Assert.Equal(ActStatus.Confirmed, typed.Status);
    }

    [Fact]
    public async Task Observation_history_resets_on_a_new_page_so_acts_after_a_long_lived_page_are_not_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        for (var i = 0; i < 30; i++)
        {
            await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        }

        // New document: its generations start again from 1, far below the old page's.
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "keys.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var target = RefOfLine(obs, "Search box");

        var res = await _engine.ActAsync(new BrowserActRequest(target, obs.Generation, "set_value", "ok"), ct);

        Assert.Equal(ActStatus.Confirmed, res.Status);
        Assert.True(res.InputSent);
    }

    // ---- P2: the test guard fails closed ----

    [Theory]
    [InlineData("http://127.0.0.1:5000/controls.html", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("about:blank", true)]
    [InlineData("data:text/html,<b>hi</b>", true)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:windowsupdate", false)]
    [InlineData("search-ms:query=test", false)]
    [InlineData("http://127.0.0.1.evil.example/", false)]
    [InlineData("https://example.com/", false)]
    public void NavigationGuard_allows_only_local_blank_and_data_urls(string url, bool allowed)
    {
        Assert.Equal(allowed, NavigationGuard.IsAllowedUrl(url));
    }

    [Fact]
    public async Task NavigationGuard_throws_before_a_non_local_navigate_is_sent()
    {
        var ct = TestContext.Current.CancellationToken;

        // The guard runs before the command is sent, so these URLs are never opened. Both are harmless even if the guard
        // ever broke: a nonexistent file, and a host under the reserved .invalid domain, which can never resolve.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.Connection!.SendAsync("Page.navigate", new { url = "file:///omniwin-guard-test-never-opened" }, null, ct));
        Assert.Contains("NavigationGuard", ex.Message);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.Connection!.SendAsync("Target.createTarget", new { url = "http://omniwin-guard-test.invalid/" }, null, ct));
        Assert.Contains("NavigationGuard", ex2.Message);
    }

    [Fact]
    public async Task NavigationGuard_is_installed_on_every_new_connection_through_the_engine_hook()
    {
        // A reconnect creates a new CdpConnection; the hook runs for it before its first command.
        Assert.NotNull(_engine.ConnectionCreated);
        await using var fresh = new CdpConnection();
        _engine.ConnectionCreated!(fresh);
        Assert.NotNull(fresh.CommandInterceptor);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fresh.CommandInterceptor!("Page.navigate", new { url = "http://omniwin-guard-test.invalid/" }));
        Assert.Contains("NavigationGuard", ex.Message);
    }

    // ---- P1: a failure after input was sent comes back as a result, never as an exception ----

    [Fact]
    public async Task Clicking_a_link_that_navigates_always_returns_a_result_with_input_sent_in_a_loop()
    {
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 50; i++)
        {
            await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "link-start.html"), ct);
            var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
            var link = RefOfLine(obs, "Go to keys page");

            var res = await _engine.ActAsync(new BrowserActRequest(link, obs.Generation, "click"), ct);

            Assert.True(res.InputSent, $"iteration {i}: {res.Status}/{res.Reason}/{res.Detail}");
        }
    }

    [Fact]
    public async Task Failure_while_verifying_after_input_returns_unverified_with_input_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var clickMe = RefOfLine(obs, "Click Me");

        // Fail the first Runtime.evaluate after the mouse is released, which is the verify scan.
        var released = false;
        var failed = false;
        var conn = _engine.Connection!;
        conn.CommandInterceptor = NavigationGuard.With((method, parameters) =>
        {
            if (method == "Input.dispatchMouseEvent" && System.Text.Json.JsonSerializer.Serialize(parameters).Contains("mouseReleased"))
            {
                released = true;
            }
            else if (released && !failed && method == "Runtime.evaluate")
            {
                failed = true;
                throw new InvalidOperationException("Simulated: page changed during verify");
            }

            return Task.CompletedTask;
        });

        ActResult res;
        try
        {
            res = await _engine.ActAsync(new BrowserActRequest(clickMe, obs.Generation, "click"), ct);
        }
        finally
        {
            conn.CommandInterceptor = NavigationGuard.Check;
        }

        Assert.True(failed);
        Assert.True(res.InputSent);
        Assert.Equal(ActStatus.Unverified, res.Status);
        Assert.Contains("verify", res.Detail ?? "");
    }

    // ---- Low: the last focus check (right before text or keys) is covered, not only the first ----

    private async Task<ActResult> ActWithFocusMovedBeforeLastCheckAsync(string action, string? value, CancellationToken ct)
    {
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "focus-late.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var fieldA = RefOfLine(obs, "Field A");
        var session = ActiveSession();

        // The first focus check passes (plain field). Just before the last check (isFocused) is sent, the page moves focus to field B.
        var moved = false;
        var conn = _engine.Connection!;
        conn.CommandInterceptor = NavigationGuard.With(async (method, parameters) =>
        {
            if (!moved && method == "Runtime.evaluate" && System.Text.Json.JsonSerializer.Serialize(parameters).Contains("isFocused("))
            {
                moved = true;
                await session.EvaluatePageScriptAsync("document.getElementById('field-b').focus()", ct);
            }
        });

        try
        {
            var res = await _engine.ActAsync(new BrowserActRequest(fieldA, obs.Generation, action, value), ct);
            Assert.True(moved, "the interceptor never saw the last focus check");
            return res;
        }
        finally
        {
            conn.CommandInterceptor = NavigationGuard.Check;
        }
    }

    [Fact]
    public async Task Type_when_focus_moves_just_before_the_last_check_sends_no_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var res = await ActWithFocusMovedBeforeLastCheckAsync("type", "leaked", ct);

        var session = ActiveSession();
        Assert.Equal("", await session.EvaluatePageScriptAsync("document.getElementById('field-b').value", ct));
        Assert.Equal("", await session.EvaluatePageScriptAsync("document.getElementById('field-a').value", ct));
        Assert.Equal(ActStatus.Error, res.Status);
        Assert.Equal(ReasonCodes.FocusFailed, res.Reason);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Press_when_focus_moves_just_before_the_last_check_sends_no_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var res = await ActWithFocusMovedBeforeLastCheckAsync("press", "Space", ct);

        Assert.Equal("0", await ActiveSession().EvaluatePageScriptAsync("window.__keys.length", ct));
        Assert.Equal(ActStatus.Error, res.Status);
        Assert.Equal(ReasonCodes.FocusFailed, res.Reason);
        Assert.True(res.InputSent);
    }

    // ---- Q6: the Risky label only records. It never blocks, asks, delays or changes an action. ----

    [Fact]
    public async Task Risk_close_tab_is_labelled_risky_and_still_runs_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("new_tab", "about:blank"), ct);

        var res = await _engine.NavigateAsync(new BrowserNavRequest("close_tab"), ct);

        Assert.Equal(RiskClass.Risky, res.Risk);
        Assert.Equal(ActStatus.Unverified, res.Status);   // same as without the label
        Assert.True(res.InputSent);
        Assert.Contains(" risk=risky", ResultFormatter.Format(res).Split((char)10)[0]);
    }

    [Fact]
    public async Task Risk_navigating_to_a_data_url_is_labelled_risky_and_still_runs_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        var res = await _engine.NavigateAsync(new BrowserNavRequest("navigate", "data:text/html,<title>ok</title><b>hi</b>"), ct);

        Assert.Equal(RiskClass.Risky, res.Risk);
        Assert.NotEqual(ActStatus.Denied, res.Status);
        Assert.NotEqual(ActStatus.Blocked, res.Status);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Risk_clicking_the_data_link_is_labelled_risky_and_still_runs_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var dataLink = RefOfLine(obs, "Data link");

        var res = await _engine.ActAsync(new BrowserActRequest(dataLink, obs.Generation, "click"), ct);

        Assert.Equal(RiskClass.Risky, res.Risk);
        Assert.NotEqual(ActStatus.Denied, res.Status);
        Assert.NotEqual(ActStatus.Blocked, res.Status);
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Risk_pressing_enter_in_the_password_field_is_labelled_risky_and_still_runs_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var password = RefOfLine(obs, "Password");

        var res = await _engine.ActAsync(new BrowserActRequest(password, obs.Generation, "press", "Enter"), ct);

        Assert.Equal(RiskClass.Risky, res.Risk);
        Assert.NotEqual(ActStatus.Denied, res.Status);
        Assert.NotEqual(ActStatus.Blocked, res.Status);
        Assert.True(res.InputSent);
        var log = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.Contains("submit:test-form", log);   // the key really reached the page
    }

    [Fact]
    public async Task Risk_normal_click_and_normal_http_navigation_are_not_labelled_risky()
    {
        var ct = TestContext.Current.CancellationToken;
        var nav = await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        Assert.True(nav.Risk is null or RiskClass.Normal);
        Assert.DoesNotContain("risk=", ResultFormatter.Format(nav));

        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var res = await _engine.ActAsync(new BrowserActRequest(RefOfLine(obs, "Click Me"), obs.Generation, "click"), ct);

        Assert.True(res.Risk is null or RiskClass.Normal);
        Assert.DoesNotContain("risk=", ResultFormatter.Format(res));
        Assert.True(res.InputSent);
    }

    [Fact]
    public async Task Risk_label_is_also_set_on_a_denied_result_and_does_not_change_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("new_tab", "about:blank"), ct);
        _killSwitch.IsTripped = true;

        var res = await _engine.NavigateAsync(new BrowserNavRequest("close_tab"), ct);

        Assert.Equal(RiskClass.Risky, res.Risk);
        Assert.Equal(ActStatus.Denied, res.Status);       // denied by the stop key, exactly as without the label
        Assert.Equal(ReasonCodes.KillSwitch, res.Reason);
        Assert.False(res.InputSent);
    }

    // ---- M2: JavaScript dialogs (Q8) ----

    private async Task<(Observation Obs, string ShowAlert, string ShowConfirm, string Plain)> LoadDialogPageAsync(CancellationToken ct)
    {
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "js-dialog.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        return (obs, RefOfLine(obs, "Show alert"), RefOfLine(obs, "Ask confirm"), RefOfLine(obs, "Plain button"));
    }

    [Fact(Timeout = 90000)]
    public async Task Alert_opened_by_a_click_does_not_hang_is_shown_in_observe_blocks_acts_and_dismiss_closes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (obs, showAlert, _, plain) = await LoadDialogPageAsync(ct);

        var click = await _engine.ActAsync(new BrowserActRequest(showAlert, obs.Generation, "click"), ct);
        Assert.True(click.InputSent);
        Assert.Equal(ActStatus.Unverified, click.Status);
        Assert.Contains("dialog", click.Detail ?? "", StringComparison.OrdinalIgnoreCase);

        var open = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        Assert.StartsWith(ResultFormatter.UntrustedHeader, open.Text);
        Assert.Contains("dialog \"Hello from alert\" modal", open.Text);
        Assert.Equal(0, open.ElementCount);

        var blocked = await _engine.ActAsync(new BrowserActRequest(plain, obs.Generation, "click"), ct);
        Assert.Equal(ActStatus.Blocked, blocked.Status);
        Assert.Equal(ReasonCodes.ModalBlocking, blocked.Reason);
        Assert.False(blocked.InputSent);

        var dismiss = await _engine.NavigateAsync(new BrowserNavRequest("dialog_dismiss"), ct);
        Assert.True(dismiss.InputSent);
        var log = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.Contains("alert:closed", log);
    }

    [Fact(Timeout = 90000)]
    public async Task Accepting_a_delete_confirmation_is_denied_and_dismissing_it_is_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (obs, _, showConfirm, _) = await LoadDialogPageAsync(ct);
        await _engine.ActAsync(new BrowserActRequest(showConfirm, obs.Generation, "click"), ct);

        var accept = await _engine.NavigateAsync(new BrowserNavRequest("dialog_accept"), ct);
        Assert.Equal(ActStatus.Denied, accept.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, accept.Reason);
        Assert.False(accept.InputSent);
        Assert.True(ActiveSession().HasOpenDialog);   // still open: nothing was sent

        var dismiss = await _engine.NavigateAsync(new BrowserNavRequest("dialog_dismiss"), ct);
        Assert.True(dismiss.InputSent);
        var log = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.Contains("confirm:false", log);
    }

    [Theory]
    [InlineData("dialog_accept")]
    [InlineData("dialog_dismiss")]
    public async Task Dialog_actions_without_an_open_dialog_are_blocked_and_send_nothing(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);

        var res = await _engine.NavigateAsync(new BrowserNavRequest(action), ct);

        Assert.Equal(ActStatus.Blocked, res.Status);
        Assert.Equal(ReasonCodes.NotFound, res.Reason);
        Assert.False(res.InputSent);
    }

    // ---- M2: tabs as windows (Q7) ----

    [Fact(Timeout = 90000)]
    public async Task Window_opened_matches_a_new_tab_by_title_and_window_closed_matches_a_closed_tab()
    {
        var ct = TestContext.Current.CancellationToken;
        var opened = await _engine.NavigateAsync(new BrowserNavRequest("new_tab", _server.BaseUrl + "keys.html",
            Expect: new ExpectSpec(new WindowOpened("Keys Test Page"), 8000)), ct);
        Assert.True(opened.Status == ActStatus.Confirmed, $"{opened.Status}/{opened.Reason}/{opened.Detail}");

        var tab = (await _engine.ListTabsAsync(ct)).First(t => t.Active).Id;
        var closed = await _engine.NavigateAsync(new BrowserNavRequest("close_tab", Tab: tab,
            Expect: new ExpectSpec(new WindowClosed(tab), 8000)), ct);
        Assert.True(closed.Status == ActStatus.Confirmed, $"{closed.Status}/{closed.Reason}/{closed.Detail}");
    }

    // ---- M2: frames (Q9) ----

    [Fact(Timeout = 90000)]
    public async Task Same_origin_frame_elements_are_indexed_with_a_frame_prefix_and_can_be_acted_on()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "frames.html"), ct);

        // The child frame loads asynchronously: observe again until its button is listed (bounded), not one fixed sleep.
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!obs.Text.Contains("Inner button") && System.Diagnostics.Stopwatch.GetElapsedTime(waitStart) < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(100, ct);
            obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        }

        Assert.Contains("frames=2", obs.Text);
        Assert.DoesNotContain("frames_not_read", obs.Text);   // once loaded, nothing is reported missing
        var innerButtonLine = obs.Text.Split((char)10).First(l => l.Contains("Inner button"));
        Assert.Contains("(f2) ", innerButtonLine);
        var innerButton = innerButtonLine.Trim().Split(' ')[0];
        var innerInput = RefOfLine(obs, "Inner input");

        var click = await _engine.ActAsync(new BrowserActRequest(innerButton, obs.Generation, "click"), ct);
        Assert.True(click.InputSent, $"{click.Status}/{click.Reason}/{click.Detail}");
        var log = await ActiveSession().EvaluatePageScriptAsync("document.getElementById('event-log').textContent", ct);
        Assert.Contains("click:inner", log);   // the real click reached the button inside the frame

        var set = await _engine.ActAsync(new BrowserActRequest(innerInput, click.After!.Generation, "set_value", "framed"), ct);
        Assert.Equal(ActStatus.Confirmed, set.Status);

        var outer = await _engine.ActAsync(new BrowserActRequest(RefOfLine(obs, "Outer button"), set.After!.Generation, "click"), ct);
        Assert.True(outer.InputSent);
    }

    [Fact(Timeout = 90000)]
    public async Task A_same_origin_frame_that_is_still_loading_is_reported_so_the_model_observes_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "frame-loading.html"), ct);

        // The fixture reopens the frame's document after the page's load event and never closes it.
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!obs.Text.Contains("frames_not_read") && System.Diagnostics.Stopwatch.GetElapsedTime(waitStart) < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(100, ct);
            obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        }

        Assert.Contains("frames=2", obs.Text);
        Assert.Contains("frames_not_read: 1 (still loading or changed while read; observe again)", obs.Text);
        Assert.Contains("Outer button", obs.Text);   // the rest of the page is still listed normally
    }

    [Fact]
    public void Frames_not_read_counts_loading_and_failed_same_origin_frames_and_never_more_than_exist()
    {
        FrameDetail[] details =
        [
            new("http://127.0.0.1/a.html", Accessible: true, Loading: true),
            new("http://127.0.0.1/b.html", Accessible: true, Loading: false),
            new("https://other.example/c.html", Accessible: false),
        ];

        Assert.Equal(0, CdpSession.FramesNotReadCount([details[1], details[2]], failedReads: 0));
        Assert.Equal(1, CdpSession.FramesNotReadCount(details, failedReads: 0));
        Assert.Equal(2, CdpSession.FramesNotReadCount(details, failedReads: 1));
        Assert.Equal(2, CdpSession.FramesNotReadCount(details, failedReads: 5));   // capped at the 2 same-origin frames
        Assert.Equal(0, CdpSession.FramesNotReadCount([], failedReads: 0));
    }

    [Fact(Timeout = 90000)]
    public async Task Cross_origin_frame_is_counted_and_listed_but_its_contents_are_not_indexed()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "frames-cross.html"), ct);
        await Task.Delay(500, ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);

        Assert.Contains("frames=2", obs.Text);
        Assert.Contains("cross_origin", obs.Text);
        Assert.DoesNotContain("Inner button", obs.Text);
    }

    [Fact]
    public async Task Observation_header_quotes_the_page_url_and_counts_one_frame_for_a_plain_page()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);

        var header = obs.Text.Split((char)10)[1];
        Assert.Contains(" url=\"http://127.0.0.1", header);
        Assert.Contains("frames=1", header);
    }

    [Fact]
    public async Task Observation_and_diff_text_use_line_feeds_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.NavigateAsync(new BrowserNavRequest("navigate", _server.BaseUrl + "controls.html"), ct);
        var obs = await _engine.ObserveAsync(new BrowserObserveRequest(), ct);
        var diff = await _engine.ObserveAsync(new BrowserObserveRequest(SinceGeneration: obs.Generation), ct);

        Assert.DoesNotContain((char)13, obs.Text);
        Assert.DoesNotContain((char)13, diff.Text);
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
