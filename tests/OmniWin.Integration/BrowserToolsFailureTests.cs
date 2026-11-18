using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Tools;

namespace OmniWin.Integration;

/// <summary>F6: a browser tool that fails while acting reports input_sent honestly (conservatively true after a stop or timeout).</summary>
public sealed class BrowserToolsFailureTests
{
    private sealed class ThrowingEngine(Func<Exception> make) : IBrowserEngine
    {
        public Task<IReadOnlyList<TabInfo>> ListTabsAsync(CancellationToken ct) => throw make();
        public Task<ActResult> NavigateAsync(BrowserNavRequest req, CancellationToken ct) => throw make();
        public Task<Observation> ObserveAsync(BrowserObserveRequest req, CancellationToken ct) => throw make();
        public Task<ActResult> ActAsync(BrowserActRequest req, CancellationToken ct) => throw make();
        public Task<WaitResult> WaitForAsync(string? tab, Expectation condition, int timeoutMs, CancellationToken ct) => throw make();
        public Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct) => throw make();
        public Task<ResolvedRef?> ResolveSelectorAsync(string urlPattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct) => throw make();
    }

    private sealed class Kill : IKillSwitch
    {
        public bool IsTripped { get; set; }
        public CancellationToken Token => CancellationToken.None;
    }

    private static string First(string text) => text.Split('\n')[0];

    [Fact]
    public async Task A_timeout_while_acting_reports_input_sent_true()
    {
        var tools = new BrowserTools(new ThrowingEngine(() => new OperationCanceledException()), new Kill(), new OmniWinConfig());

        var act = First(await tools.BrowserAct("b1", 1, "click", TestContext.Current.CancellationToken));
        var nav = First(await tools.BrowserNav("reload", TestContext.Current.CancellationToken));

        Assert.Contains("status=error", act);
        Assert.Contains("input_sent=true", act);
        Assert.Contains("reason=engine_timeout", act);
        Assert.Contains("input_sent=true", nav);
    }

    [Fact]
    public async Task A_stop_while_acting_reports_input_sent_true_with_kill_switch()
    {
        var tools = new BrowserTools(new ThrowingEngine(() => new OperationCanceledException()), new Kill { IsTripped = true }, new OmniWinConfig());

        var act = First(await tools.BrowserAct("b1", 1, "click", TestContext.Current.CancellationToken));

        Assert.Contains("reason=kill_switch", act);
        Assert.Contains("input_sent=true", act);
    }

    [Fact]
    public async Task A_failure_in_a_read_tool_reports_input_sent_false()
    {
        var tools = new BrowserTools(new ThrowingEngine(() => new OperationCanceledException()), new Kill(), new OmniWinConfig());

        var observe = First(await tools.BrowserObserve(TestContext.Current.CancellationToken));
        var tabs = First(await tools.BrowserTabs(TestContext.Current.CancellationToken));

        Assert.Contains("input_sent=false", observe);
        Assert.Contains("input_sent=false", tabs);
    }

    [Fact]
    public async Task A_non_timeout_failure_before_input_still_reports_input_sent_false()
    {
        var tools = new BrowserTools(new ThrowingEngine(() => new InvalidOperationException("no browser")), new Kill(), new OmniWinConfig());

        var act = First(await tools.BrowserAct("b1", 1, "click", TestContext.Current.CancellationToken));

        Assert.Contains("input_sent=false", act);
    }
}
