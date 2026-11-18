namespace OmniWin.Core;

// ---------- browser engine (contract §7). WS2 implements in OmniWin.Browser. ----------

public interface IBrowserEngine
{
    Task<IReadOnlyList<TabInfo>> ListTabsAsync(CancellationToken ct);
    Task<ActResult> NavigateAsync(BrowserNavRequest req, CancellationToken ct);
    Task<Observation> ObserveAsync(BrowserObserveRequest req, CancellationToken ct);
    Task<ActResult> ActAsync(BrowserActRequest req, CancellationToken ct);
    Task<WaitResult> WaitForAsync(string? tab, Expectation condition, int timeoutMs, CancellationToken ct);
    Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct);
    /// <summary>Returns a fresh ref and the generation of the scan that found it (recorded in history), or null when nothing matches.</summary>
    Task<ResolvedRef?> ResolveSelectorAsync(string urlPattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct);
}

/// <summary>Action: navigate | back | forward | reload | new_tab | close_tab | switch_tab.</summary>
public sealed record BrowserNavRequest(string Action, string? Url = null, string? Tab = null, ExpectSpec? Expect = null);

public sealed record BrowserObserveRequest(string? Tab = null, int MaxElements = 150, long? SinceGeneration = null,
                                           bool IncludeText = false, bool ViewportOnly = false);

/// <summary>Action: click | double_click | type | set_value | clear | select | check | uncheck | hover | press | focus | scroll_into_view.</summary>
public sealed record BrowserActRequest(string Ref, long Generation, string Action, string? Value = null, ExpectSpec? Expect = null);
