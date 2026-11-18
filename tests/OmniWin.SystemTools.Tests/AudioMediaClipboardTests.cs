using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

public sealed class AudioMediaClipboardTests
{
    // ---- audio ----

    [Fact]
    public async Task Audio_read_returns_the_state()
    {
        var h = new Harness();

        var state = await h.Service.GetAudioAsync(Harness.Ct);

        Assert.Equal(new AudioState(40, false, "Test Speakers"), state);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task Audio_read_without_a_device_is_blocked_not_found_not_an_exception()
    {
        var h = new Harness();
        h.Audio.NoDevice = true;

        var outcome = await h.Service.GetAudioOutcomeAsync(Harness.Ct);

        Assert.Equal(ActStatus.Blocked, outcome.Failure!.Status);
        Assert.Equal(ReasonCodes.NotFound, outcome.Failure.Reason);
        Assert.False(outcome.Failure.InputSent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task A_volume_outside_0_to_100_is_refused_and_nothing_is_set(int volume)
    {
        var h = new Harness();

        var result = await h.Service.SetAudioAsync(volume, null, Harness.Ct);

        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Log);
        Assert.Equal(40, h.Audio.Volume);
    }

    [Fact]
    public async Task Setting_neither_volume_nor_mute_is_refused()
    {
        var h = new Harness();

        var result = await h.Service.SetAudioAsync(null, null, Harness.Ct);

        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
    }

    [Fact]
    public async Task Setting_volume_and_mute_goes_through_the_gate_and_is_read_back()
    {
        var h = new Harness();

        var result = await h.Service.SetAudioAsync(25, true, Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(25, h.Audio.Volume);
        Assert.True(h.Audio.Muted);
        Assert.Equal(["gate", "input:volume", "input:mute"], h.Log);
        var descriptor = Assert.Single(h.Gate.Seen);
        Assert.Equal("system", descriptor.Engine);
        Assert.Equal("audio", descriptor.Tool);
    }

    [Fact]
    public async Task A_volume_the_device_ignores_is_contradicted()
    {
        var h = new Harness();
        h.Audio.Ignore = true;

        var result = await h.Service.SetAudioAsync(10, null, Harness.Ct);

        Assert.Equal(ActStatus.Contradicted, result.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, result.Reason);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task Audio_is_not_paused_for_user_activity_but_the_stop_key_stops_it()
    {
        var h = new Harness();
        h.Activity.UserActiveRecently = true;

        var allowed = await h.Service.SetAudioAsync(30, null, Harness.Ct);
        Assert.Equal(ActStatus.Confirmed, allowed.Status);

        h.Kill.IsTripped = true;
        var stopped = await h.Service.SetAudioAsync(35, null, Harness.Ct);
        Assert.Equal(ReasonCodes.KillSwitch, stopped.Reason);
        Assert.False(stopped.InputSent);
        Assert.Equal(30, h.Audio.Volume);
    }

    [Fact]
    public async Task A_gate_denial_leaves_the_volume_alone()
    {
        var h = new Harness();
        h.Gate.Decide = _ => new GateResult(false, RiskClass.Forbidden, ReasonCodes.PolicyForbidden, false, "no");

        var result = await h.Service.SetAudioAsync(5, null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.False(result.InputSent);
        Assert.Equal(40, h.Audio.Volume);
    }

    // ---- media ----

    [Fact]
    public async Task An_unknown_media_action_is_refused()
    {
        var h = new Harness();

        var result = await h.Service.MediaAsync("rewind", Harness.Ct);

        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
    }

    [Fact]
    public async Task Media_without_a_session_is_blocked_and_sends_nothing()
    {
        var h = new Harness();
        h.Media.State = null;

        var result = await h.Service.MediaAsync("play_pause", Harness.Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.NotFound, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task Play_pause_is_confirmed_when_the_state_changes()
    {
        var h = new Harness();
        h.Media.StateAfterSend = MediaPlayback.Paused;

        var result = await h.Service.MediaAsync("play_pause", Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(["gate", "input:media:play_pause"], h.Log);
    }

    [Fact]
    public async Task Play_pause_that_changes_nothing_is_unverified()
    {
        var h = new Harness();

        var result = await h.Service.MediaAsync("play_pause", Harness.Ct);

        Assert.Equal(ActStatus.Unverified, result.Status);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task Pause_when_it_is_already_paused_sends_nothing_and_is_confirmed()
    {
        var h = new Harness();
        h.Media.State = MediaPlayback.Paused;

        var result = await h.Service.MediaAsync("pause", Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.False(result.InputSent);
        Assert.Null(result.Channel);
        Assert.DoesNotContain("input:media:play_pause", h.Log);
    }

    [Fact]
    public async Task Play_when_it_is_already_playing_sends_nothing_and_is_confirmed()
    {
        var h = new Harness();   // the fake starts as Playing

        var result = await h.Service.MediaAsync("play", Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.False(result.InputSent);
        Assert.DoesNotContain("input:media:play_pause", h.Log);
    }

    [Fact]
    public async Task Pause_while_playing_toggles_once_and_is_confirmed_only_when_it_reached_paused()
    {
        var h = new Harness();
        h.Media.StateAfterSend = MediaPlayback.Paused;

        var result = await h.Service.MediaAsync("pause", Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal(["gate", "input:media:play_pause"], h.Log);
    }

    [Fact]
    public async Task Pause_that_does_not_reach_paused_is_unverified_not_confirmed()
    {
        var h = new Harness();   // the state never changes

        var result = await h.Service.MediaAsync("pause", Harness.Ct);

        Assert.Equal(ActStatus.Unverified, result.Status);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task Skipping_has_no_state_to_read_so_it_is_unverified()
    {
        var h = new Harness();

        var result = await h.Service.MediaAsync("next", Harness.Ct);

        Assert.Equal(ActStatus.Unverified, result.Status);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task A_refusing_session_is_an_error_with_input_sent()
    {
        var h = new Harness();
        h.Media.Accept = false;

        var result = await h.Service.MediaAsync("stop", Harness.Ct);

        Assert.Equal(ActStatus.Error, result.Status);
        Assert.True(result.InputSent);
    }

    // ---- system_info ----

    [Fact]
    public async Task An_unknown_system_info_kind_is_refused_without_an_exception()
    {
        var h = new Harness();

        var outcome = await h.Service.SystemInfoOutcomeAsync("secrets", Harness.Ct);

        Assert.Equal(ReasonCodes.UnsupportedAction, outcome.Failure!.Reason);
        Assert.False(outcome.Failure.InputSent);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("battery")]
    [InlineData("displays")]
    [InlineData("TIME")]
    public async Task Known_system_info_kinds_return_text(string kind)
    {
        var h = new Harness();

        var text = await h.Service.SystemInfoAsync(kind, Harness.Ct);

        Assert.Equal("info:" + kind.ToLowerInvariant(), text);
    }

    // ---- clipboard ----

    [Fact]
    public async Task Clipboard_set_gives_the_gate_only_a_length_never_the_text()
    {
        var h = new Harness();
        const string secret = "correct horse battery staple";

        var result = await h.Service.ClipboardSetAsync(secret, Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.Equal(secret, h.Clipboard.Text);
        var descriptor = Assert.Single(h.Gate.Seen);
        Assert.Equal($"<{secret.Length} chars>", descriptor.ValuePreview);
        Assert.DoesNotContain("horse", System.Text.Json.JsonSerializer.Serialize(descriptor));
        Assert.DoesNotContain("horse", result.Detail ?? string.Empty);
        Assert.Equal(["gate", "input:clipboard"], h.Log);
    }

    [Fact]
    public async Task Clipboard_set_that_does_not_read_back_is_contradicted()
    {
        var h = new Harness();
        h.Clipboard.Corrupt = true;

        var result = await h.Service.ClipboardSetAsync("abc", Harness.Ct);

        Assert.Equal(ActStatus.Contradicted, result.Status);
        Assert.Equal(ReasonCodes.ReadbackMismatch, result.Reason);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task Clipboard_set_refuses_text_over_the_limit()
    {
        var h = new Harness();

        var result = await h.Service.ClipboardSetAsync(new string('x', SystemToolsService.MaxClipboardSetChars + 1), Harness.Ct);

        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
        Assert.Equal("hello", h.Clipboard.Text);
    }

    [Fact]
    public async Task Clipboard_get_returns_the_text_through_the_gate_and_is_labelled_risky()
    {
        var h = new Harness();

        var outcome = await h.Service.ClipboardGetOutcomeAsync(Harness.Ct);

        Assert.True(outcome.Ok);
        Assert.Equal(new ClipboardText("hello", 5, false), outcome.Value);
        var descriptor = Assert.Single(h.Gate.Seen);
        Assert.Equal("clipboard_get", descriptor.Action);
        Assert.Equal("system", descriptor.Engine);
        Assert.Equal("hello", await h.Service.ClipboardGetAsync(Harness.Ct));
    }

    [Fact]
    public async Task Clipboard_get_cuts_long_text_and_says_so()
    {
        var h = new Harness();
        h.Clipboard.Text = new string('a', SystemToolsService.MaxClipboardChars + 50);

        var outcome = await h.Service.ClipboardGetOutcomeAsync(Harness.Ct);

        Assert.True(outcome.Value!.Truncated);
        Assert.Equal(SystemToolsService.MaxClipboardChars, outcome.Value.Text.Length);
        Assert.Equal(SystemToolsService.MaxClipboardChars + 50, outcome.Value.Chars);
    }

    [Fact]
    public async Task Clipboard_get_with_nothing_on_it_returns_empty_text()
    {
        var h = new Harness();
        h.Clipboard.Text = null;

        var outcome = await h.Service.ClipboardGetOutcomeAsync(Harness.Ct);

        Assert.Equal(string.Empty, outcome.Value!.Text);
    }

    [Fact]
    public async Task Clipboard_get_denied_by_the_gate_returns_the_denial_with_the_risky_label()
    {
        var h = new Harness();
        h.Gate.Decide = _ => new GateResult(false, RiskClass.Forbidden, ReasonCodes.UserDenied, false, "declined");

        var outcome = await h.Service.ClipboardGetOutcomeAsync(Harness.Ct);

        Assert.False(outcome.Ok);
        Assert.Equal(ActStatus.Denied, outcome.Failure!.Status);
        Assert.Equal(ReasonCodes.UserDenied, outcome.Failure.Reason);
        Assert.False(outcome.Failure.InputSent);
        Assert.Equal(RiskClass.Risky, outcome.Failure.Risk);
    }

    [Fact]
    public async Task The_plain_clipboard_get_throws_a_fault_that_carries_the_denial()
    {
        var h = new Harness();
        h.Gate.Decide = _ => new GateResult(false, RiskClass.Forbidden, ReasonCodes.ConfirmTimeout, false, "timed out");

        var fault = await Assert.ThrowsAsync<SystemToolFault>(() => h.Service.ClipboardGetAsync(Harness.Ct));

        Assert.Equal(ReasonCodes.ConfirmTimeout, fault.Result.Reason);
    }

    [Fact]
    public async Task Clipboard_is_not_paused_for_user_activity()
    {
        var h = new Harness();
        h.Activity.UserActiveRecently = true;

        var result = await h.Service.ClipboardSetAsync("x", Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
    }
}
