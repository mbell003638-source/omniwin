using OmniWin.Core;

namespace OmniWin.Ocr.Tests;

public sealed class OcrServiceTests
{
    [Fact]
    public async Task With_no_window_or_region_the_foreground_window_is_read_and_words_get_screen_boxes_and_ids()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "notepad.exe", "Untitled");
        s.AddWindow("w2", 200, "fixture.exe", "Fixture", foreground: true);

        var outcome = await s.Service.RecognizeOutcomeAsync(null, null, Setup.Ct);

        Assert.True(outcome.Ok);
        var capture = outcome.Value!;
        Assert.Equal("w2", capture.Target);
        Assert.Equal(1, s.Capture.WindowCaptures);

        // The fake capture sits at (1000,500): word boxes are origin + image position, in physical pixels.
        var save = capture.Result.Words[0];
        Assert.Equal("Save", save.Text);
        Assert.Equal(new ScreenRect(1010, 520, 40, 14), save.Box);
        Assert.Equal(["o1", "o2", "o3"], capture.Result.Words.Select(w => w.Id).ToArray());
        Assert.Equal("Save As\nCancel", capture.Result.Text);

        // Every id resolves to the captured window, for desktop_act.
        Assert.True(s.Refs.TryResolve("o3", out var word, out var hwnd, out _));
        Assert.Equal("Cancel", word.Text);
        Assert.Equal(200, hwnd);
    }

    [Fact]
    public async Task Ids_keep_counting_across_captures()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture", foreground: true);

        await s.Service.RecognizeOutcomeAsync(null, null, Setup.Ct);
        var second = await s.Service.RecognizeOutcomeAsync(null, null, Setup.Ct);

        Assert.Equal(["o4", "o5", "o6"], second.Value!.Result.Words.Select(w => w.Id).ToArray());
    }

    [Theory]
    [InlineData("w1")]
    [InlineData("fixture")]
    [InlineData("fixture.exe")]
    [InlineData("Fixture win")]
    public async Task The_window_argument_accepts_an_id_a_process_or_a_title_part(string selector)
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture window");

        var outcome = await s.Service.RecognizeOutcomeAsync(selector, null, Setup.Ct);

        Assert.True(outcome.Ok);
        Assert.Equal("w1", outcome.Value!.Target);
    }

    [Fact]
    public async Task An_unknown_or_ambiguous_window_is_blocked_not_found_and_nothing_is_captured()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "One");
        s.AddWindow("w2", 200, "fixture.exe", "Two");

        var unknown = await s.Service.RecognizeOutcomeAsync("nothing-like-this", null, Setup.Ct);
        var ambiguous = await s.Service.RecognizeOutcomeAsync("fixture", null, Setup.Ct);

        Assert.Equal(ReasonCodes.NotFound, unknown.Failure!.Reason);
        Assert.Equal(ReasonCodes.NotFound, ambiguous.Failure!.Reason);
        Assert.Contains("w1", ambiguous.Failure.Detail);
        Assert.Equal(0, s.Capture.WindowCaptures);
    }

    [Fact]
    public async Task No_foreground_window_and_no_arguments_is_blocked()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "One");

        var outcome = await s.Service.RecognizeOutcomeAsync(null, null, Setup.Ct);

        Assert.Equal(ActStatus.Blocked, outcome.Failure!.Status);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    [InlineData(30000, 10)]
    public async Task A_bad_region_is_refused_before_anything_else(int width, int height)
    {
        var s = new Setup();

        var outcome = await s.Service.RecognizeOutcomeAsync(null, new ScreenRect(0, 0, width, height), Setup.Ct);

        Assert.Equal(ReasonCodes.UnsupportedAction, outcome.Failure!.Reason);
        Assert.False(outcome.Failure.InputSent);
        Assert.Equal(0, s.Capture.RegionCaptures);
    }

    // ---- forbidden windows ----

    [Fact]
    public async Task OmniWins_own_window_is_denied_and_never_captured()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "OmniWin.Server.exe", "OmniWin", pid: Environment.ProcessId);

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(ActStatus.Denied, outcome.Failure!.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, outcome.Failure.Reason);
        Assert.Equal(0, s.Capture.WindowCaptures);
        Assert.Empty(s.Gate.Seen);
    }

    [Theory]
    [InlineData("elevated", ActStatus.Blocked, "elevated_target")]
    [InlineData("secure_desktop", ActStatus.Blocked, "secure_desktop")]
    [InlineData("blocked_by_policy", ActStatus.Denied, "policy_forbidden")]
    public async Task Elevated_secure_and_blocklisted_windows_are_refused_and_never_captured(string inaccessible, ActStatus status, string reason)
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "LogonUI.exe", "Sign in", inaccessible: inaccessible);

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(status, outcome.Failure!.Status);
        Assert.Equal(reason, outcome.Failure.Reason);
        Assert.False(outcome.Failure.InputSent);
        Assert.Equal(0, s.Capture.WindowCaptures);
    }

    [Fact]
    public async Task A_minimized_window_is_blocked()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture", minimized: true);

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(ActStatus.Blocked, outcome.Failure!.Status);
        Assert.Equal(ReasonCodes.Offscreen, outcome.Failure.Reason);
    }

    [Fact]
    public async Task The_gate_sees_an_ocr_descriptor_and_a_denial_stops_before_capture()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture window");
        s.Gate.Decide = _ => new GateResult(false, RiskClass.Forbidden, ReasonCodes.PolicyForbidden, false, "no");

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        var descriptor = Assert.Single(s.Gate.Seen);
        Assert.Equal("ocr", descriptor.Engine);
        Assert.Equal("screen_ocr", descriptor.Tool);
        Assert.Equal("fixture.exe", descriptor.Process);
        Assert.Null(descriptor.Target);
        Assert.Equal(ActStatus.Denied, outcome.Failure!.Status);
        Assert.Equal(0, s.Capture.WindowCaptures);
        Assert.Equal(0, s.Engine.Calls);
    }

    // ---- regions ----

    [Fact]
    public async Task A_region_capture_masks_forbidden_windows_only()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Ordinary", rect: new ScreenRect(0, 0, 500, 500));
        s.AddWindow("w2", 200, "LogonUI.exe", "Sign in", inaccessible: "blocked_by_policy", rect: new ScreenRect(600, 0, 300, 300));
        s.AddWindow("w3", 300, "OmniWin.Server.exe", "OmniWin", pid: Environment.ProcessId, rect: new ScreenRect(1000, 0, 200, 200));
        s.AddWindow("w4", 400, "taskmgr.exe", "Task Manager", inaccessible: "elevated", rect: new ScreenRect(1300, 0, 200, 200));

        var outcome = await s.Service.RecognizeOutcomeAsync(null, new ScreenRect(0, 0, 1600, 400), Setup.Ct);

        Assert.True(outcome.Ok);
        Assert.Equal(1, s.Capture.RegionCaptures);
        Assert.Equal([new ScreenRect(600, 0, 300, 300), new ScreenRect(1000, 0, 200, 200)], s.Capture.LastMasks);
    }

    [Fact]
    public async Task Region_words_take_the_top_level_window_under_their_box_and_words_over_no_window_get_no_id()
    {
        var s = new Setup();
        s.Capture.WindowAt = (x, _) => x < 1100 ? 777 : 0;

        var outcome = await s.Service.RecognizeOutcomeAsync(null, new ScreenRect(1000, 500, 400, 100), Setup.Ct);

        var words = outcome.Value!.Result.Words;
        Assert.Equal("Save", words[0].Text);
        Assert.NotNull(words[0].Id);
        Assert.True(s.Refs.TryResolve(words[0].Id!, out _, out var hwnd, out _));
        Assert.Equal(777, hwnd);
        Assert.Null(words[2].Id); // "Cancel" sits at x=1200: no window there
        Assert.Contains("region_1000,500_400x100", OcrFormat.Render(outcome.Value, outcome.Timing));
    }

    [Fact]
    public async Task A_window_and_a_region_together_keep_only_the_words_inside_the_region()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");

        // Window at (1000,500). Region covers only the "Save As" line (x 1010..1090).
        var outcome = await s.Service.RecognizeOutcomeAsync("w1", new ScreenRect(1000, 500, 120, 60), Setup.Ct);

        Assert.Equal(["Save", "As"], outcome.Value!.Result.Words.Select(w => w.Text).ToArray());
    }

    // ---- limits and failures ----

    [Fact]
    public async Task A_capture_larger_than_the_engine_accepts_is_blocked_with_the_limit_in_the_detail()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");
        s.Engine.MaxImageDimension = 300;

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(ActStatus.Blocked, outcome.Failure!.Status);
        Assert.Contains("300", outcome.Failure.Detail);
        Assert.Equal(0, s.Engine.Calls);
    }

    [Fact]
    public async Task A_window_that_cannot_be_captured_is_blocked()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");
        s.Capture.Image = null;

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(ActStatus.Blocked, outcome.Failure!.Status);
    }

    [Fact]
    public async Task Missing_ocr_languages_are_reported_not_thrown()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");
        s.Engine.Throw = new OcrUnavailableException("Windows OCR has no language available for this user profile.");

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(ActStatus.Blocked, outcome.Failure!.Status);
        Assert.Contains("language", outcome.Failure.Detail);
    }

    [Fact]
    public async Task An_unexpected_engine_exception_never_escapes()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");
        s.Engine.Throw = new InvalidOperationException("boom");

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Failure!.InputSent);
    }

    [Fact]
    public async Task A_hung_engine_returns_engine_timeout_with_input_sent_false()
    {
        var s = new Setup(new OcrOptions { OperationTimeoutMs = 150 });
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");
        s.Engine.Hook = async ct => await Task.Delay(Timeout.Infinite, ct);

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(ActStatus.Error, outcome.Failure!.Status);
        Assert.Equal(ReasonCodes.EngineTimeout, outcome.Failure.Reason);
        Assert.False(outcome.Failure.InputSent);
    }

    [Fact]
    public async Task The_stop_key_denies_before_anything_is_captured()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");
        s.Kill.IsTripped = true;

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(ActStatus.Denied, outcome.Failure!.Status);
        Assert.Equal(ReasonCodes.KillSwitch, outcome.Failure.Reason);
        Assert.Equal(0, s.Capture.WindowCaptures);
    }

    [Fact]
    public async Task The_stop_key_tripped_mid_recognition_denies_and_registers_no_words()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");
        s.Engine.Hook = async ct =>
        {
            s.Kill.Trip();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        Assert.Equal(ReasonCodes.KillSwitch, outcome.Failure!.Reason);
        Assert.Equal(0, s.Refs.Count);
    }

    [Fact]
    public async Task The_plain_interface_method_returns_the_result_and_throws_a_fault_with_the_denial()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture", foreground: true);
        IOcrFallback plain = s.Service;

        var result = await plain.RecognizeAsync(null, null, Setup.Ct);
        Assert.Equal(3, result.Words.Count);

        s.Kill.IsTripped = true;
        var fault = await Assert.ThrowsAsync<OcrFault>(() => plain.RecognizeAsync(null, null, Setup.Ct));
        Assert.Equal(ReasonCodes.KillSwitch, fault.Result.Reason);
    }

    [Fact]
    public async Task Timings_use_contract_phase_keys_and_the_metric_excludes_the_gate()
    {
        var s = new Setup();
        s.AddWindow("w1", 100, "fixture.exe", "Fixture");

        var outcome = await s.Service.RecognizeOutcomeAsync("w1", null, Setup.Ct);

        foreach (var phase in new[] { "resolve", "precheck", "gate", "read", "format" })
        {
            Assert.True(outcome.Timing.PhasesMs.ContainsKey(phase), phase);
        }

        var metric = Assert.Single(s.Metrics.Records);
        Assert.Equal("screen_ocr", metric.Op);
    }

    [Fact]
    public void The_component_reports_ready()
    {
        var s = new Setup();

        Assert.Equal("ocr", s.Service.Component);
        Assert.Equal("ready", s.Service.State);
    }
}
