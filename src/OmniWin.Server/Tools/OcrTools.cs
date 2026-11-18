using System.ComponentModel;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Ocr;

namespace OmniWin.Server.Tools;

/// <summary>WS3 <c>screen_ocr</c> (contract §8). Text only: no image is ever returned to the model.</summary>
[McpServerToolType]
internal sealed class OcrTools(OcrService ocr)
{
    [McpServerTool(Name = "screen_ocr", Title = "Read text on screen (OCR)", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("Read the text of a window or a screen region with Windows OCR when UI Automation can't see it (images, canvases, remote views). " +
        "window is a wN id, process name or unique title substring; region is {x,y,width,height} in physical screen pixels. " +
        "With neither, the foreground window is read. Windows of OmniWin, sign-in prompts and the secure desktop are denied or blacked out. " +
        "Each word gets an id oN; desktop_act can click an oN ref (click, double_click, right_click only) within 10 seconds of the capture. " +
        ToolDocs.FallbackOrder + " " + ToolDocs.UntrustedContent)]
    public async Task<string> ScreenOcr(CancellationToken ct, string? window = null, ScreenRect? region = null)
    {
        try
        {
            var outcome = await ocr.RecognizeOutcomeAsync(window, region, ct).ConfigureAwait(false);
            if (outcome.Failure is { } failure) return ResultFormatter.Format(failure);

            return OcrFormat.Render(outcome.Value!, outcome.Timing);
        }
        catch (Exception)
        {
            return ResultFormatter.Format(ActResult.Failed(false, ReasonCodes.NotFound,
                "Windows could not complete the screen read.", Timing.Total(0)));
        }
    }
}
