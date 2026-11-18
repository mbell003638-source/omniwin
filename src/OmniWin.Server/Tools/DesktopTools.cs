using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Desktop;
using OmniWin.Safety;

namespace OmniWin.Server.Tools;

[McpServerToolType]
internal sealed class DesktopTools(IDesktopEngine desktop, OmniWinConfig config)
{
    [McpServerTool(Name = "windows_list", Title = "List desktop windows", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("List top-level Windows desktop windows and their stable window IDs, foreground, modal, and accessibility states. " +
        ToolDocs.UntrustedContent)]
    public Task<string> WindowsList(CancellationToken ct) => ReadAsync(async () =>
    {
        var windows = await desktop.ListWindowsAsync(ct).ConfigureAwait(false);
        var text = new StringBuilder(ResultFormatter.UntrustedHeader);
        foreach (var window in windows)
        {
            text.Append('\n').Append(window.Id).Append(' ').Append(ResultFormatter.OneLine(window.Process))
                .Append(" pid=").Append(window.Pid.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(ResultFormatter.Quote(window.Title, ResultFormatter.MaxNameChars));
            if (window.IsForeground) text.Append(" foreground");
            if (window.IsMinimized) text.Append(" minimized");
            if (window.IsModal) text.Append(" modal");
            if (window.OwnerId is { } owner) text.Append(" owner=").Append(owner);
            if (window.Inaccessible is { } inaccessible) text.Append(" inaccessible=").Append(inaccessible);
        }

        return text.ToString();
    });

    [McpServerTool(Name = "desktop_observe", Title = "Observe desktop controls", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("Observe a window's UI Automation controls with stable element refs and a generation. " +
        "window accepts a wN ID, process name, or unique title substring. Omit it for the foreground window. " +
        "scope is window, focused, or dialog. max_elements is capped at 1000. since_gen requests a diff when retained. " +
        ToolDocs.FallbackOrder + " " +
        ToolDocs.UntrustedContent)]
    public Task<string> DesktopObserve(CancellationToken ct, string? window = null, string scope = "window",
        int? max_elements = null, long? since_gen = null, bool include_text = false) =>
        ReadAsync(async () => (await desktop.ObserveAsync(new DesktopObserveRequest(window, scope,
            max_elements ?? config.Observe.DefaultMaxElements, since_gen, include_text), ct).ConfigureAwait(false)).Text);

    [McpServerTool(Name = "desktop_act", Title = "Act on a desktop control", ReadOnly = false, Idempotent = false, Destructive = true)]
    [Description("Act on a ref from desktop_observe using its gen. Actions are click, double_click, right_click, invoke, toggle, " +
        "select, expand, collapse, set_value, type, clear, focus, scroll_into_view, hover, drag. hover moves the pointer onto the control " +
        "and presses nothing. drag presses on ref, moves, and releases on the control whose ref (same gen) is given in value; the button " +
        "is always released, and dropping onto the Recycle Bin or a delete action is refused. type inserts at the selection, " +
        "set_value replaces and verifies the whole value. type verifies prior text with the captured selection replaced. " +
        "Unavailable selection information yields unverified. " +
        "Some controls require Tab or Enter to commit a UIA value change; value readback alone does not verify an application-level save or navigation. " +
        "RichEditD2DPT typing rejects supplementary Unicode and newline packets; use explicit set_value for full replacement or desktop_keys Enter between supported text runs. " +
        "invoke, set_value, clear, toggle, select, expand, collapse, and scroll_into_view address UIA controls " +
        "in background windows while the user works in another window. An app can change focus as part of its own response. " +
        "set_value and clear on a file dialog's File name box or on Explorer's address bar are never confirmed (unverified): " +
        "those controls ignore a value set this way, so use type there (select all first), press Enter, and check the result. " +
        "OCR oN refs support clicks. " + ToolDocs.ExpectParameter + " " + ToolDocs.RetryRules)]
    public async Task<string> DesktopAct(string @ref, long gen, string action, CancellationToken ct,
        string? value = null, ExpectSpec? expect = null) =>
        ResultFormatter.Format(await desktop.ActAsync(new DesktopActRequest(@ref, gen, action, value, expect), ct).ConfigureAwait(false));

    [McpServerTool(Name = "desktop_keys", Title = "Send focus-locked keys", ReadOnly = false, Idempotent = false, Destructive = true)]
    [Description("Send key chords such as ctrl+s, enter, alt+f4, or ctrl+a backspace. Separate chords with spaces. " +
        "Keyboard input is locked to the verified window and focused control. A focus change stops the sequence. " +
        ToolDocs.ExpectParameter + " " + ToolDocs.RetryRules)]
    public async Task<string> DesktopKeys(string keys, CancellationToken ct, string? window = null, ExpectSpec? expect = null) =>
        ResultFormatter.Format(await desktop.SendKeysAsync(new KeysRequest(keys, window, expect), ct).ConfigureAwait(false));

    [McpServerTool(Name = "desktop_scroll", Title = "Scroll a desktop control", ReadOnly = false, Idempotent = false, Destructive = false)]
    [Description("Scroll at a verified point in a control or window. direction is up, down, left, or right. " +
        "amount is a positive number of wheel notches. ref and window must refer to the same window when both are given. " +
        ToolDocs.RetryRules)]
    public async Task<string> DesktopScroll(string direction, int amount, CancellationToken ct, string? @ref = null, string? window = null) =>
        ResultFormatter.Format(await desktop.ScrollAsync(new ScrollRequest(@ref, window, direction, amount), ct).ConfigureAwait(false));

    private static async Task<string> ReadAsync(Func<Task<string>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (DesktopFault ex)
        {
            return ResultFormatter.Format(new ActResult(ex.Status, false, ex.Reason, ex.Message, null, null, Timing.Total(0)));
        }
        catch (Exception)
        {
            return ResultFormatter.Format(ActResult.Failed(false, ReasonCodes.EngineTimeout,
                "Windows could not complete the requested desktop read.", Timing.Total(0)));
        }
    }
}
