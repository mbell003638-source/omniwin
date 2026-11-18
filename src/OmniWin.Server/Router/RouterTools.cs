using System.ComponentModel;
using ModelContextProtocol.Server;
using OmniWin.Core;

namespace OmniWin.Server.Router;

[McpServerToolType]
internal sealed class RouterTools(RouterService router)
{
    [McpServerTool(Name = "omniwin_do", Title = "Run a simple command locally", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Try a short, simple request first, with no model call: pause, play, next, previous, volume 30, volume up, mute, unmute, " +
        "switch to <window>, minimize/maximize/restore/close <window>, open <app>, run workflow <name>, what time is it, battery. It runs " +
        "only an exact, unambiguous match, through OmniWin's own tools (so every check applies), and returns that tool's result line. " +
        "Anything else comes back as needs_brain with nothing sent: handle it yourself with the other tools. Calling it first costs " +
        "almost nothing. " + ToolDocs.RetryRules)]
    public Task<string> OmniwinDo(string text, CancellationToken ct) => router.DoAsync(text, ct);
}
