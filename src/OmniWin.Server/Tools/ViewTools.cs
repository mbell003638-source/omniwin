using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OmniWin.Core;

namespace OmniWin.Server.Tools;

/// <summary>
/// <c>screen_view</c> and <c>browser_view</c> (owner decision 2026-11-08): look-only pictures for what the element lists
/// can't describe. They take no coordinate to act on and give nothing a later act could target. The image goes only to the
/// caller; the audit filter reads the first text block, so no image data is ever logged.
/// </summary>
[McpServerToolType]
internal sealed class ViewTools(IScreenViewer screen, IBrowserViewer browser)
{
    [McpServerTool(Name = "screen_view", Title = "Look at a window or the screen", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("A look-only picture of one window (default: the foreground window), a screen region, or the whole screen (window \"screen\"). " +
        "Use it only when desktop_observe and screen_ocr don't show what you need, for example to understand a picture, a chart or a canvas app. " +
        "It cannot click, type or point at anything: to act, go back to desktop_observe refs or screen_ocr oN refs. " +
        "window is a wN id, process name, unique title substring or \"screen\"; region is {x,y,width,height} in physical screen pixels; " +
        "labels=true (one window only) draws the refs of that window's last desktop_observe on the picture, each element boxed with its number, " +
        "so a vision model can read which ref is which and then act through desktop_act with the refs_gen shown in the result. " +
        "max_side is the longest side in pixels (default 1280, max 1920). Password fields and OmniWin's own, sign-in and secure-desktop windows are blacked out; " +
        "if that can't be done there is no image. No window is brought to the front. " + ToolDocs.FallbackOrder + " " +
        ViewFormat.ImageIsData + " " + ToolDocs.UntrustedContent)]
    public async Task<CallToolResult> ScreenView(CancellationToken ct, string? window = null, ScreenRect? region = null, int? max_side = null,
        bool labels = false)
    {
        try
        {
            return ToResult(await screen.ViewAsync(window, region, max_side, labels, ct).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return TextOnly(ResultFormatter.Format(ActResult.Failed(false, ReasonCodes.NotFound,
                "Windows could not complete the screen view.", Timing.Total(0))));
        }
    }

    [McpServerTool(Name = "browser_view", Title = "Look at a browser tab", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("A look-only picture of a tab of OmniWin's dedicated browser (default: the active tab; full_page captures the whole page). " +
        "Use it only when browser_observe doesn't show what you need, for example to understand a chart, a canvas or an image. " +
        "It cannot click, type or point at anything: to act, go back to browser_observe refs. " +
        "tab is a tN id from browser_tabs; max_side is the longest side in pixels (default 1280, max 1920). " +
        "Password and payment fields, and cross-origin frames, are blacked out; if that can't be done there is no image. " +
        ToolDocs.FallbackOrder + " " + ViewFormat.ImageIsData + " " + ToolDocs.UntrustedContent)]
    public async Task<CallToolResult> BrowserView(CancellationToken ct, string? tab = null, bool full_page = false, int? max_side = null)
    {
        try
        {
            return ToResult(await browser.ViewTabAsync(tab, full_page, max_side, ct).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return TextOnly(ResultFormatter.Format(ActResult.Failed(false, ReasonCodes.BrowserNotConnected,
                "The browser could not complete the view.", Timing.Total(0))));
        }
    }

    private static CallToolResult ToResult(ViewOutcome outcome)
    {
        if (!outcome.Ok)
        {
            return TextOnly(ResultFormatter.Format(outcome.Failure ?? ActResult.Failed(false, ReasonCodes.NotFound, "No image.", Timing.Total(0))));
        }

        var image = outcome.Image!;
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = ViewFormat.Render(outcome) },
                ImageContentBlock.FromBytes(image.Data, image.MediaType),
            ],
        };
    }

    private static CallToolResult TextOnly(string text) =>
        new() { Content = [new TextContentBlock { Text = text }] };
}
