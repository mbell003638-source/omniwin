using Microsoft.Extensions.Logging.Abstractions;

namespace OmniWin.Safety.Tests;

public sealed class KillSwitchTests
{
    [Fact]
    public void Toggle_cancels_old_token_and_resume_issues_a_fresh_token()
    {
        using var stop = Create();
        var original = stop.Token;
        Assert.False(original.IsCancellationRequested);
        stop.Toggle();
        Assert.True(stop.IsTripped);
        Assert.True(original.IsCancellationRequested);
        stop.Toggle();
        Assert.False(stop.IsTripped);
        Assert.False(stop.Token.IsCancellationRequested);
        Assert.True(original.IsCancellationRequested);
        var called = false;
        using var registration = original.Register(() => called = true);
        Assert.True(called);
    }

    [Fact]
    public void Shutdown_is_idempotent_and_cancels_current_token()
    {
        var stop = Create();
        var token = stop.Token;
        stop.Dispose();
        stop.Dispose();
        stop.Toggle();
        Assert.True(token.IsCancellationRequested);
        Assert.True(stop.IsTripped);
        Assert.Equal("off", stop.State);
    }

    [Fact]
    public async Task Concurrent_toggles_and_token_reads_are_consistent()
    {
        using var stop = Create();
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            var token = stop.Token;
            stop.Toggle();
            using var registration = token.Register(static () => { });
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);
        Assert.False(stop.IsTripped);
        Assert.False(stop.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData("ctrl+alt+shift+k")]
    [InlineData("SHIFT+CONTROL+ALT+K")]
    [InlineData("win+f24")]
    [InlineData("alt+delete")]
    public void Valid_hotkeys_are_parsed_without_registering_them(string value) =>
        Assert.True(KillSwitch.TryParseHotkey(value, out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("k")]
    [InlineData("ctrl+")]
    [InlineData("ctrl++k")]
    [InlineData("ctrl+control+k")]
    [InlineData("ctrl+k+x")]
    [InlineData("ctrl+f25")]
    public void Invalid_hotkeys_are_rejected(string value) =>
        Assert.False(KillSwitch.TryParseHotkey(value, out _, out _));

    [Fact]
    public void Real_input_starts_the_cooldown_but_injected_input_does_not()
    {
        var clock = new ManualTime();
        var activity = new UserActivityTracker(3000, clock);
        activity.Record(injected: true);
        Assert.False(activity.UserActiveRecently);
        Assert.Equal(DateTimeOffset.MinValue, activity.LastRealInput);
        activity.Record(injected: false);
        Assert.True(activity.UserActiveRecently);
        Assert.Equal(clock.GetUtcNow(), activity.LastRealInput);
        clock.Advance(2999);
        Assert.True(activity.UserActiveRecently);
        clock.Advance(1);
        Assert.False(activity.UserActiveRecently);
        activity.Record(injected: true);
        Assert.False(activity.UserActiveRecently);
    }

    [Fact]
    public void Zero_cooldown_records_activity_without_pausing()
    {
        var activity = new UserActivityTracker(0, new ManualTime());
        activity.Record(injected: false);
        Assert.False(activity.UserActiveRecently);
        Assert.NotEqual(DateTimeOffset.MinValue, activity.LastRealInput);
    }

    private static KillSwitch Create() => new(new OmniWinConfig(), NullLogger<KillSwitch>.Instance, startThread: false);

    private sealed class ManualTime : TimeProvider
    {
        private long _milliseconds;
        private readonly DateTimeOffset _start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public override DateTimeOffset GetUtcNow() => _start.AddMilliseconds(_milliseconds);
        internal void Advance(int milliseconds) => _milliseconds += milliseconds;
    }
}
