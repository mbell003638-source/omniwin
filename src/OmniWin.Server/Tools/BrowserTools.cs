using System.ComponentModel;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Server.Tools;

/// <summary>
/// The four browser tools (contract §8). The engine starts lazily on the first call and uses OmniWin's own dedicated
/// browser profile. Handlers never throw: any failure becomes a formatted result with an accurate <c>input_sent</c>.
/// Every call is bounded by a timeout and by the stop key.
/// </summary>
[McpServerToolType]
internal sealed class BrowserTools(IBrowserEngine browser, IKillSwitch killSwitch, OmniWinConfig config)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NavTimeout = TimeSpan.FromSeconds(60);

    [McpServerTool(Name = "browser_tabs", Title = "List browser tabs", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("List the tabs of OmniWin's own dedicated browser (it is started on first use): one line per tab with its tN id, " +
        "quoted title and URL, and an active marker. " + ToolDocs.UntrustedContent)]
    public async Task<string> BrowserTabs(CancellationToken ct)
    {
        try
        {
            using var cts = Linked(ct, ReadTimeout);
            return ResultFormatter.FormatTabs(await browser.ListTabsAsync(cts.Token).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [McpServerTool(Name = "browser_nav", Title = "Navigate the browser", ReadOnly = false, Idempotent = false, Destructive = true)]
    [Description("Navigate OmniWin's dedicated browser. action is navigate, back, forward, reload, new_tab, close_tab, switch_tab, " +
        "dialog_dismiss or dialog_accept. url is required for navigate and optional for new_tab. tab is a tN id from browser_tabs " +
        "(default: the active tab). A JavaScript dialog blocks the page: browser_observe shows its text, and dialog_dismiss or " +
        "dialog_accept answers it (accepting a dialog about deleting is refused by the no-delete rule). " +
        ToolDocs.ExpectParameter + " " + ToolDocs.RetryRules)]
    public async Task<string> BrowserNav(string action, CancellationToken ct, string? url = null, string? tab = null, ExpectSpec? expect = null)
    {
        try
        {
            using var cts = Linked(ct, NavTimeout);
            return ResultFormatter.Format(await browser.NavigateAsync(new BrowserNavRequest(action, url, tab, expect), cts.Token).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Failure(ex, mayHaveSentInput: true);
        }
    }

    [McpServerTool(Name = "browser_observe", Title = "Observe a browser tab", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("Observe a tab of OmniWin's dedicated browser: interactive elements with stable bN refs and a generation. " +
        "tab is a tN id (default: the active tab). max_elements is capped at 1000. since_gen requests a diff when retained. " +
        "viewport_only limits the list to what is on screen. " + ToolDocs.FallbackOrder + " " + ToolDocs.UntrustedContent)]
    public async Task<string> BrowserObserve(CancellationToken ct, string? tab = null, int? max_elements = null, long? since_gen = null,
        bool include_text = false, bool viewport_only = false)
    {
        try
        {
            using var cts = Linked(ct, ReadTimeout);
            var max = Math.Clamp(max_elements ?? config.Observe.DefaultMaxElements, 1, 1000);
            var observation = await browser.ObserveAsync(new BrowserObserveRequest(tab, max, since_gen, include_text, viewport_only), cts.Token).ConfigureAwait(false);
            return observation.Text;
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [McpServerTool(Name = "browser_act", Title = "Act on a browser element", ReadOnly = false, Idempotent = false, Destructive = true)]
    [Description("Act on a ref from browser_observe using its gen. Actions are click, double_click, type, set_value, clear, select, " +
        "check, uncheck, hover, press, focus, scroll_into_view. type inserts at the cursor, set_value replaces the whole value and " +
        "verifies it, press sends one key (Enter, Tab, Escape, Space, arrows) to the ref after focusing it. The element is re-read " +
        "live and compared with the observation first; a changed, moved or covered element is stale or blocked and nothing is sent. " +
        ToolDocs.ExpectParameter + " " + ToolDocs.RetryRules)]
    public async Task<string> BrowserAct(string @ref, long gen, string action, CancellationToken ct, string? value = null, ExpectSpec? expect = null)
    {
        try
        {
            using var cts = Linked(ct, ActTimeout);
            return ResultFormatter.Format(await browser.ActAsync(new BrowserActRequest(@ref, gen, action, value, expect), cts.Token).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Failure(ex, mayHaveSentInput: true);
        }
    }

    private CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, killSwitch.Token);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>
    /// Formats a failure. A stop or a timeout while a browser_act or browser_nav was running can happen after input was
    /// already sent, so for those calls the result conservatively reports input_sent=true (contract 13.3). Reads never send input.
    /// </summary>
    private string Failure(Exception ex, bool mayHaveSentInput = false)
    {
        string reason;
        string detail;
        if (killSwitch.IsTripped)
        {
            reason = ReasonCodes.KillSwitch;
            detail = "OmniWin is stopped";
        }
        else if (ex is OperationCanceledException or TimeoutException)
        {
            reason = ReasonCodes.EngineTimeout;
            detail = "The browser did not answer in time";
        }
        else
        {
            reason = ReasonCodes.BrowserNotConnected;
            detail = "The browser could not complete the request";
        }

        var interrupted = reason is ReasonCodes.KillSwitch or ReasonCodes.EngineTimeout;
        return ResultFormatter.Format(ActResult.Failed(mayHaveSentInput && interrupted, reason, detail, Timing.Total(0)));
    }
}
